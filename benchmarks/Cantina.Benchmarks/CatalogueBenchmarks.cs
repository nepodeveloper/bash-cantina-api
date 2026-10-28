using BenchmarkDotNet.Attributes;
using Cantina.Application;
using Cantina.Domain;
using Cantina.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cantina.Benchmarks;

[MemoryDiagnoser]
public class CatalogueBenchmarks
{
    private const int WarmRequestsPerBatch = 50_000;
    private static readonly Guid StoreId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly CatalogueQuery Query = new(
        Search: "benchmark", Kind: MenuItemKind.Dish, MinPrice: 20, MaxPrice: 80);
    private readonly string schema = "benchmark_" + Guid.NewGuid().ToString("N");
    private readonly CatalogueCache coldCache = new();
    private readonly CatalogueCache warmCache = new();
    private DbContextOptions<CantinaDbContext> options = null!;
    private string connectionString = "";
    private bool schemaCreated;

    [Params(10_000)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var supplied = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (string.IsNullOrWhiteSpace(supplied))
            throw new InvalidOperationException(
                "Set ConnectionStrings__DefaultConnection to a dedicated PostgreSQL database named cantina_benchmarks. " +
                "Never use a production database. Setup creates a unique schema and cleanup drops only that schema.");
        var builder = new NpgsqlConnectionStringBuilder(supplied);
        if (builder.Database != "cantina_benchmarks")
            throw new InvalidOperationException("For safety, the database must be named exactly cantina_benchmarks.");
        builder.SearchPath = schema;
        connectionString = builder.ConnectionString;
        options = new DbContextOptionsBuilder<CantinaDbContext>().UseNpgsql(connectionString).Options;

        try
        {
            await using var db = CreateContext();
            await db.Database.OpenConnectionAsync();
            await using var createSchema = db.Database.GetDbConnection().CreateCommand();
            // The identifier is generated locally from a GUID, never supplied by a connection string or user.
            createSchema.CommandText = $"""CREATE SCHEMA "{schema}" """;
            await createSchema.ExecuteNonQueryAsync();
            schemaCreated = true;
            // SearchPath confines both migration history and application tables to this run's schema.
            await db.Database.MigrateAsync();
            db.Stores.Add(new Store { Id = StoreId, Slug = "benchmark", Name = "Benchmark store" });
            db.Users.AddRange(
                new ApplicationUser { Id = "benchmark-customer-0", UserName = "benchmark-customer-0" },
                new ApplicationUser { Id = "benchmark-customer-1", UserName = "benchmark-customer-1" });
            for (var index = 0; index < ItemCount; index++)
            {
                var id = Guid.Parse($"dddddddd-dddd-dddd-dddd-{index:x12}");
                db.MenuItems.Add(new MenuItem
                {
                    Id = id,
                    StoreId = StoreId,
                    Kind = index % 2 == 0 ? MenuItemKind.Dish : MenuItemKind.Drink,
                    Name = $"Item {index / 4:D5}",
                    Description = $"Deterministic benchmark menu item {index:D5}.",
                    Price = 10 + index % 100,
                    ImageUrl = "https://images.example.com/benchmark.jpg",
                    Ratings = Enumerable.Range(0, index % 3).Select(customer => new Rating
                    {
                        StoreId = StoreId,
                        MenuItemId = id,
                        CustomerId = $"benchmark-customer-{customer}",
                        Score = 1 + (index + customer) % 5
                    }).ToList()
                });
            }
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""ANALYZE "MenuItems"; ANALYZE "Ratings";""");

            var baseline = await TrackedFullMaterialization();
            var optimized = await NoTrackingPagedProjection();
            await WarmCache();
            var cached = await ReadWarmCache();
            AssertSamePage(baseline, optimized);
            AssertSamePage(baseline, cached);
            Console.WriteLine(
                $"Correctness passed: {ItemCount:N0} items, {await db.Ratings.CountAsync():N0} ratings; " +
                $"{baseline.TotalCount:N0} matches, {baseline.Items.Count} complete DTOs with ratings. Schema: {schema}.");
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<Page<MenuItemDto>> TrackedFullMaterialization()
    {
        await using var db = CreateContext();
        var all = await db.MenuItems.AsTracking().Include(item => item.Ratings)
            .Where(item => item.StoreId == StoreId).ToListAsync();
        var filtered = all.Where(item =>
            (item.Name.Contains(Query.Search!, StringComparison.OrdinalIgnoreCase) ||
             item.Description.Contains(Query.Search!, StringComparison.OrdinalIgnoreCase)) &&
            item.Kind == Query.Kind && item.Price >= Query.MinPrice && item.Price <= Query.MaxPrice).ToList();
        var page = filtered.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id)
            .Skip((Query.Page - 1) * Query.PageSize).Take(Query.PageSize)
            .Select(item => new MenuItemDto(item.Id, item.StoreId, item.Kind, item.Name,
                item.Description, item.Price, item.ImageUrl,
                item.Ratings.Average(rating => (double?)rating.Score), item.Ratings.Count))
            .ToList().AsReadOnly();
        return new Page<MenuItemDto>(page, Query.Page, Query.PageSize, filtered.Count);
    }

    [Benchmark]
    public async Task<Page<MenuItemDto>> NoTrackingPagedProjection()
    {
        // Force a real repository miss, including the small invalidation cost in the measurement.
        coldCache.Invalidate(StoreId);
        await using var db = CreateContext();
        IMenuRepository repository = new MenuRepository(db, coldCache);
        var result = await repository.SearchAsync(StoreId, Query, CancellationToken.None);
        if (db.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("The optimized read unexpectedly tracked entities.");
        return result;
    }

    [IterationSetup(Target = nameof(WarmBoundedCache))]
    public void PrepareWarmIteration() => WarmCache().GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = WarmRequestsPerBatch)]
    public async Task<Page<MenuItemDto>> WarmBoundedCache()
    {
        Page<MenuItemDto> result = null!;
        // Batching amortizes timer overhead when IterationSetup forces one invocation per iteration.
        for (var index = 0; index < WarmRequestsPerBatch; index++)
            result = await ReadWarmCache();
        return result;
    }

    private async Task<Page<MenuItemDto>> ReadWarmCache()
    {
        await using var db = CreateContext();
        IMenuRepository repository = new MenuRepository(db, warmCache);
        return await repository.SearchAsync(StoreId, Query, CancellationToken.None);
    }

    private async Task WarmCache()
    {
        warmCache.Invalidate(StoreId);
        await ReadWarmCache();
    }

    private CantinaDbContext CreateContext() => new(options);

    private static void AssertSamePage(Page<MenuItemDto> expected, Page<MenuItemDto> actual)
    {
        if (expected.PageNumber != actual.PageNumber || expected.PageSize != actual.PageSize ||
            expected.TotalCount != actual.TotalCount || !expected.Items.SequenceEqual(actual.Items) ||
            expected.Items.Count != Query.PageSize)
            throw new InvalidOperationException("Benchmark paths returned different pages or incomplete DTOs.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        coldCache.Dispose();
        warmCache.Dispose();
        if (!schemaCreated) return;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""DROP SCHEMA "{schema}" CASCADE""";
        await command.ExecuteNonQueryAsync();
        schemaCreated = false;
    }
}
