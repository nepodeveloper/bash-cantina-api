using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cantina.Application;
using Cantina.Domain;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Cantina.IntegrationTests;

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;

[CollectionDefinition("rate-limit")]
public sealed class RateLimitCollection : ICollectionFixture<RateLimitFixture>;

[CollectionDefinition("catalogue-rate-limit")]
public sealed class CatalogueRateLimitCollection : ICollectionFixture<CatalogueRateLimitFixture>;

public class ApiFixture : IAsyncLifetime
{
    public const string Password = "Integration!Password123";
    public const string BashSlug = "bash";
    public const string SportsceneSlug = "sportscene";
    public static readonly Guid Bash = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Sportscene = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly int authPermitLimit;
    private readonly int cataloguePermitLimit;
    private TestApplication? application;

    public ApiFixture() : this(1000, 1000) { }
    protected ApiFixture(int authPermitLimit, int cataloguePermitLimit) =>
        (this.authPermitLimit, this.cataloguePermitLimit) = (authPermitLimit, cataloguePermitLimit);

    public IServiceProvider Services => application!.Services;

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        application = new TestApplication(database.GetConnectionString(), authPermitLimit, cataloguePermitLimit);
        using var client = CreateClient();
        await ExpectAsync(await client.GetAsync("/health/live"), HttpStatusCode.OK);
    }

    public async Task DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        await database.DisposeAsync();
    }

    public HttpClient CreateClient(string? token = null)
    {
        var client = application!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<(string Email, TokenPair Tokens)> RegisterAsync(string? name = null)
    {
        var email = $"customer-{Guid.NewGuid():N}@example.com";
        using var client = CreateClient();
        await ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name = name ?? "Integration Customer", email, password = Password }), HttpStatusCode.Accepted);
        return (email, await LoginAsync(email));
    }

    public async Task<HttpClient> CustomerAsync() => CreateClient((await RegisterAsync()).Tokens.AccessToken);

    public async Task<TokenPair> LoginAsync(string email)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        await ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TokenPair>())!;
    }

    public async Task<HttpClient> StaffAsync(string role, params Guid[] stores)
    {
        var email = $"{role}-{Guid.NewGuid():N}@example.com";
        await using (var scope = Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, Name = $"Integration {role}" };
            AssertSucceeded(await users.CreateAsync(user, Password));
            AssertSucceeded(await users.AddToRoleAsync(user, role));
            var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
            foreach (var store in stores)
                db.StoreMemberships.Add(new StoreMembership { StoreId = store, UserId = user.Id });
            await db.SaveChangesAsync();
        }
        return CreateClient((await LoginAsync(email)).AccessToken);
    }

    public static string Menu(string storeSlug) => $"/api/v1/stores/{storeSlug}/menu-items";
    public static MenuItemInput Input(string? name = null, MenuItemKind kind = MenuItemKind.Dish, decimal price = 42.50m) =>
        new(kind, name ?? $"Item-{Guid.NewGuid():N}", "Freshly prepared integration meal.",
            price, "https://images.example.com/integration.jpg");

    public static async Task<MenuItemDto> CreateItemAsync(HttpClient client, string storeSlug, MenuItemInput? input = null)
    {
        var response = await client.PostAsJsonAsync(Menu(storeSlug), input ?? Input());
        await ExpectAsync(response, HttpStatusCode.Created);
        Assert.NotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<MenuItemDto>())!;
    }

    public static async Task ExpectAsync(HttpResponseMessage response, params HttpStatusCode[] statuses) =>
        Assert.True(statuses.Contains(response.StatusCode),
            $"Expected {string.Join("/", statuses)}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static void AssertSucceeded(IdentityResult result) =>
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(x => x.Description)));

    private sealed class TestApplication(string connectionString, int authLimit, int catalogueLimit)
        : WebApplicationFactory<Program>
    {
        private Dictionary<string, string?> Settings => new()
        {
            ["Database:Provider"] = "PostgreSQL",
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["Database:ApplyMigrations"] = "true",
            ["Authentication:RateLimit:PermitLimit"] = authLimit.ToString(),
            ["Catalogue:RateLimit:PermitLimit"] = catalogueLimit.ToString()
        };

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(Settings));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>(Settings) { ["Logging:LogLevel:Default"] = "Warning" }));
        }
    }
}

public sealed class RateLimitFixture() : ApiFixture(2, 1000);
public sealed class CatalogueRateLimitFixture() : ApiFixture(1000, 4);
public sealed record TokenPair(string AccessToken, string RefreshToken);
