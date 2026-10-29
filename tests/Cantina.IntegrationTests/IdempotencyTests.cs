using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Cantina.Api;
using Cantina.Application;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cantina.IntegrationTests;

[Collection("api")]
public sealed class IdempotencyTests(ApiFixture fixture)
{
    [Fact]
    public async Task Menu_create_replays_the_committed_response_and_rejects_a_changed_body()
    {
        using var admin = await fixture.StaffAsync("Admin");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var input = ApiFixture.Input();
        var route = ApiFixture.Menu(ApiFixture.BashSlug);
        var first = await admin.PostAsJsonAsync(route, input);
        await ApiFixture.ExpectAsync(first, HttpStatusCode.Created);
        var replay = await admin.PostAsJsonAsync(route, input);
        await ApiFixture.ExpectAsync(replay, HttpStatusCode.Created);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
        await ApiFixture.ExpectAsync(await admin.PostAsJsonAsync(route, input with { Price = input.Price + 1 }),
            HttpStatusCode.UnprocessableEntity);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        Assert.Equal(1, await db.MenuItems.CountAsync(item => item.Name == input.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_keys_are_reusable_even_without_another_completed_request(bool completed)
    {
        var userId = await NewUserAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        var key = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId = userId, Key = key, Fingerprint = "old", Owner = Guid.NewGuid(),
            CreatedAtUtc = now - IdempotencyStore.Retention - TimeSpan.FromSeconds(1),
            CompletedAtUtc = completed ? now - IdempotencyStore.Retention : null,
            StatusCode = completed ? 201 : 0, Body = completed ? "old response" : null
        });
        await db.SaveChangesAsync();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        Assert.IsType<IdempotencyState.Reserved>(await store.BeginAsync(userId, key, "new", default));
        var current = await db.IdempotencyRecords.AsNoTracking().SingleAsync(record => record.UserId == userId && record.Key == key);
        Assert.Null(current.CompletedAtUtc);
        Assert.Null(current.Body);
        Assert.Equal("new", current.Fingerprint);
    }

    [Fact]
    public async Task Abandoned_reservations_recover_without_allowing_the_old_owner_to_write_or_release()
    {
        var userId = await NewUserAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        var clock = new ReplayClock();
        var store = new IdempotencyStore(db, clock, scope.ServiceProvider.GetRequiredService<CatalogueCache>());
        var key = Guid.NewGuid().ToString("N");
        var old = Assert.IsType<IdempotencyState.Reserved>(await store.BeginAsync(userId, key, "body", default));
        Assert.IsType<IdempotencyState.InProgress>(await store.BeginAsync(userId, key, "body", default));
        clock.Now += IdempotencyStore.ReservationLifetime;
        var current = Assert.IsType<IdempotencyState.Reserved>(await store.BeginAsync(userId, key, "body", default));
        Assert.NotEqual(old.Owner, current.Owner);
        await store.ReleaseAsync(userId, key, old.Owner, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync(userId, key, old.Owner,
            () => throw new Xunit.Sdk.XunitException("An expired owner must not execute business writes."), default));
        var response = new IdempotentResponse(201, "/created", "saved");
        Assert.Equal(response, await store.ExecuteAsync(userId, key, current.Owner, () => Task.FromResult(response), default));
        await store.ReleaseAsync(userId, key, current.Owner, default);
        Assert.Equal(response, Assert.IsType<IdempotencyState.Completed>(
            await store.BeginAsync(userId, key, "body", default)).Response);
    }

    [Fact]
    public async Task Concurrent_reservers_have_exactly_one_owner()
    {
        var userId = await NewUserAsync();
        var key = Guid.NewGuid().ToString("N");
        var states = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IIdempotencyStore>().BeginAsync(userId, key, "body", default);
        }));
        Assert.Single(states.OfType<IdempotencyState.Reserved>());
        Assert.Equal(7, states.OfType<IdempotencyState.InProgress>().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_capture_or_completion_rolls_back_the_business_write(bool failCompletion)
    {
        var userId = await NewUserAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<CantinaDbContext>().Database.GetConnectionString();
        var options = new DbContextOptionsBuilder<CantinaDbContext>().UseNpgsql(connection);
        if (failCompletion) options.AddInterceptors(new CompletionFailure());
        await using var db = new CantinaDbContext(options.Options);
        var cache = scope.ServiceProvider.GetRequiredService<CatalogueCache>();
        var store = new IdempotencyStore(db, TimeProvider.System, cache);
        var repository = new MenuRepository(db, cache, TimeProvider.System);
        var key = Guid.NewGuid().ToString("N");
        var owner = Assert.IsType<IdempotencyState.Reserved>(await store.BeginAsync(userId, key, "body", default)).Owner;
        var input = ApiFixture.Input();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync(userId, key, owner, async () =>
        {
            await repository.CreateAsync(ApiFixture.Bash, input, default);
            if (!failCompletion) throw new InvalidOperationException("response capture failed");
            return new IdempotentResponse(201, "/created", "response");
        }, default));
        var verification = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        Assert.False(await verification.MenuItems.AnyAsync(item => item.Name == input.Name));
        Assert.Null((await verification.IdempotencyRecords.AsNoTracking()
            .SingleAsync(record => record.UserId == userId && record.Key == key)).CompletedAtUtc);
        await store.ReleaseAsync(userId, key, owner, default);
        Assert.IsType<IdempotencyState.Reserved>(await store.BeginAsync(userId, key, "body", default));
    }

    private async Task<string> NewUserAsync()
    {
        var (email, _) = await fixture.RegisterAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CantinaDbContext>().Users
            .Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
    }

    private sealed class ReplayClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CompletionFailure : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"IdempotencyRecords\"", StringComparison.Ordinal))
                throw new InvalidOperationException("replay completion failed");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class IdempotencyDeliveryTests
{
    [Fact]
    public async Task Delivery_failure_keeps_the_committed_replay_and_never_reexecutes_the_handler()
    {
        using var services = new ServiceCollection().AddLogging().ConfigureHttpJsonOptions(_ => { }).BuildServiceProvider();
        var store = new ReplayStore();
        var filter = new IdempotencyFilter<MenuItemInput>(store, NullLogger<IdempotencyFilter<MenuItemInput>>.Instance);
        var context = Context(services, new DisconnectedStream());
        var result = Assert.IsAssignableFrom<IResult>(await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context, ApiFixture.Input("same body")),
            _ => ValueTask.FromResult<object?>(Results.Created("/created", new { id = "created-once" }))));
        Assert.NotNull(store.Completed);
        await Assert.ThrowsAsync<IOException>(() => result.ExecuteAsync(context));
        Assert.Equal(0, store.Releases);

        var replay = Context(services, new MemoryStream());
        var replayResult = Assert.IsAssignableFrom<IResult>(await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(replay, ApiFixture.Input("same body")),
            _ => throw new Xunit.Sdk.XunitException("Committed writes must not execute again.")));
        await replayResult.ExecuteAsync(replay);
        Assert.Equal(201, replay.Response.StatusCode);
        Assert.Equal("/created", replay.Response.Headers.Location);
        Assert.Equal("true", replay.Response.Headers["Idempotency-Replayed"]);
        Assert.Equal(store.Completed.Body, Encoding.UTF8.GetString(((MemoryStream)replay.Response.Body).ToArray()));
    }

    private static DefaultHttpContext Context(IServiceProvider services, Stream body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test"));
        context.Request.Headers["Idempotency-Key"] = "key";
        context.Request.Method = "POST";
        context.Request.Path = "/menu-items";
        context.Response.Body = body;
        return context;
    }

    private sealed class ReplayStore : IIdempotencyStore
    {
        public IdempotentResponse? Completed { get; private set; }
        public int Releases { get; private set; }
        public Task<IdempotencyState> BeginAsync(string userId, string key, string fingerprint, CancellationToken ct) =>
            Task.FromResult<IdempotencyState>(Completed is null
                ? new IdempotencyState.Reserved(Guid.NewGuid()) : new IdempotencyState.Completed(Completed));
        public async Task<IdempotentResponse> ExecuteAsync(string userId, string key, Guid owner,
            Func<Task<IdempotentResponse>> write, CancellationToken ct) => Completed = await write();
        public Task ReleaseAsync(string userId, string key, Guid owner, CancellationToken ct)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }

    private sealed class DisconnectedStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("client disconnected"));
    }
}