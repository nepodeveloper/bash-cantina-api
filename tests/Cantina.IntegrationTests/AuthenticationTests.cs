using System.Net;
using System.Net.Http.Json;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cantina.IntegrationTests;

[Collection("api")]
public sealed class AuthenticationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Register_login_refresh_and_me_use_real_identity_tokens()
    {
        var (email, tokens) = await fixture.RegisterAsync();
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        using var authenticated = fixture.CreateClient(tokens.AccessToken);
        var me = await authenticated.GetAsync("/api/v1/auth/me");
        await ApiFixture.ExpectAsync(me, HttpStatusCode.OK);
        Assert.Contains(email, await me.Content.ReadAsStringAsync());
        using var anonymous = fixture.CreateClient();
        var refreshed = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { tokens.RefreshToken });
        await ApiFixture.ExpectAsync(refreshed, HttpStatusCode.OK);
        var newTokens = (await refreshed.Content.ReadFromJsonAsync<TokenPair>())!;
        using var refreshedClient = fixture.CreateClient(newTokens.AccessToken);
        await ApiFixture.ExpectAsync(await refreshedClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Public_registration_never_accepts_privileged_role_or_store_membership()
    {
        using var client = fixture.CreateClient();
        var email = $"privilege-{Guid.NewGuid():N}@example.com";
        var registration = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = ApiFixture.Password, role = "Admin", storeId = ApiFixture.Bash });
        await ApiFixture.ExpectAsync(registration, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.NoContent);
        var tokens = await fixture.LoginAsync(email);
        using var customer = fixture.CreateClient(tokens.AccessToken);
        await ApiFixture.ExpectAsync(await customer.PostAsJsonAsync(ApiFixture.Menu(ApiFixture.Bash), ApiFixture.Input()),
            HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_and_tampered_access_tokens_cannot_access_me()
    {
        using var anonymous = fixture.CreateClient();
        await ApiFixture.ExpectAsync(await anonymous.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        var (_, tokens) = await fixture.RegisterAsync();
        using var tampered = fixture.CreateClient("invalid" + tokens.AccessToken);
        await ApiFixture.ExpectAsync(await tampered.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tampered_refresh_token_is_rejected()
    {
        var (_, tokens) = await fixture.RegisterAsync();
        using var client = fixture.CreateClient();
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = "invalid" + tokens.RefreshToken }), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Weak_password_is_rejected_and_duplicate_registration_does_not_create_another_account()
    {
        using var client = fixture.CreateClient();
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"weak-{Guid.NewGuid():N}@example.com", password = "weak" }), HttpStatusCode.BadRequest);
        var (email, _) = await fixture.RegisterAsync();
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = ApiFixture.Password }), HttpStatusCode.Accepted);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        Assert.Single(db.Users.Where(user => user.Email == email));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_account_even_when_correct_password_is_then_supplied()
    {
        var (email, _) = await fixture.RegisterAsync();
        using var client = fixture.CreateClient();
        for (var attempt = 0; attempt < 5; attempt++)
            await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email, password = "Incorrect!Password123" }), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = ApiFixture.Password }), HttpStatusCode.Unauthorized);
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByEmailAsync(email))!));
    }
}

[Collection("rate-limit")]
public sealed class AuthenticationRateLimitTests(RateLimitFixture fixture)
{
    [Fact]
    public async Task Authentication_requests_are_limited_and_return_retry_after()
    {
        using var client = fixture.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
            await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email = "unknown@example.com", password = ApiFixture.Password }), HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.42");
        var limited = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "unknown@example.com", password = ApiFixture.Password });
        await ApiFixture.ExpectAsync(limited, HttpStatusCode.TooManyRequests);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.True(limited.Headers.RetryAfter!.Delta > TimeSpan.Zero ||
            limited.Headers.RetryAfter.Date > DateTimeOffset.UtcNow);
    }
}
