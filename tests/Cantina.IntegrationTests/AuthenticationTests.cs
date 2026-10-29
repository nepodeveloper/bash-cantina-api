using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Cantina.Api;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cantina.IntegrationTests;

[Collection("api")]
public sealed class AuthenticationTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("two-factor", HttpStatusCode.Unauthorized)]
    [InlineData("stamp", HttpStatusCode.Unauthorized)]
    [InlineData("logout", HttpStatusCode.Unauthorized)]
    public async Task External_exchange_requires_current_stamp_and_no_pending_second_factor(string change, HttpStatusCode expected)
    {
        var (email, tokens) = await fixture.RegisterAsync();
        string code;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            code = scope.ServiceProvider.GetRequiredService<ExternalLoginCodes>()
                .Issue(user.Id, await users.GetSecurityStampAsync(user));
            if (change == "two-factor") Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
            if (change == "stamp") Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }
        using var client = fixture.CreateClient(tokens.AccessToken);
        if (change == "logout")
            await ApiFixture.ExpectAsync(await client.PostAsync("/api/v1/auth/logout", null), HttpStatusCode.NoContent);
        var response = await client.PostAsJsonAsync("/api/v1/auth/external/exchange", new { code });
        await ApiFixture.ExpectAsync(response, expected);
        if (expected == HttpStatusCode.OK)
        {
            var exchanged = (await response.Content.ReadFromJsonAsync<TokenPair>())!;
            using var authenticated = fixture.CreateClient(exchanged.AccessToken);
            await ApiFixture.ExpectAsync(await authenticated.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        }
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/external/exchange", new { code }),
            HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_login_refresh_and_me_use_real_identity_tokens()
    {
        var (email, tokens) = await fixture.RegisterAsync("Chalmun the Wookiee");
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        using var authenticated = fixture.CreateClient(tokens.AccessToken);
        var me = await authenticated.GetAsync("/api/v1/auth/me");
        await ApiFixture.ExpectAsync(me, HttpStatusCode.OK);
        var identity = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, identity.GetProperty("email").GetString());
        Assert.Equal("Chalmun the Wookiee", identity.GetProperty("name").GetString());
        using var anonymous = fixture.CreateClient();
        var refreshed = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { tokens.RefreshToken });
        await ApiFixture.ExpectAsync(refreshed, HttpStatusCode.OK);
        var newTokens = (await refreshed.Content.ReadFromJsonAsync<TokenPair>())!;
        using var refreshedClient = fixture.CreateClient(newTokens.AccessToken);
        await ApiFixture.ExpectAsync(await refreshedClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Registration_requires_a_name(string? name)
    {
        using var client = fixture.CreateClient();
        var email = $"nameless-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name, email, password = ApiFixture.Password });
        await ApiFixture.ExpectAsync(response, HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(problem.GetProperty("errors").GetProperty("name").EnumerateArray());
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByEmailAsync(email));
    }

    [Fact]
    public async Task Registration_rejects_a_name_longer_than_the_column()
    {
        using var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            name = new string('a', 121),
            email = $"long-name-{Guid.NewGuid():N}@example.com",
            password = ApiFixture.Password
        });
        await ApiFixture.ExpectAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Public_registration_never_accepts_privileged_role_or_store_membership()
    {
        using var client = fixture.CreateClient();
        var email = $"privilege-{Guid.NewGuid():N}@example.com";
        var registration = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Privilege Probe", email, password = ApiFixture.Password, role = "Admin", storeId = ApiFixture.Bash });
        await ApiFixture.ExpectAsync(registration, HttpStatusCode.Accepted);
        var tokens = await fixture.LoginAsync(email);
        using var customer = fixture.CreateClient(tokens.AccessToken);
        await ApiFixture.ExpectAsync(await customer.PostAsJsonAsync(ApiFixture.Menu(ApiFixture.BashSlug), ApiFixture.Input()),
            HttpStatusCode.Forbidden);
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        Assert.Equal("Customer", Assert.Single(await users.GetRolesAsync(user)));
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        Assert.False(await db.StoreMemberships.AnyAsync(membership => membership.UserId == user.Id));
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
            new { name = "Weak Password", email = $"weak-{Guid.NewGuid():N}@example.com", password = "weak" }), HttpStatusCode.BadRequest);
        var email = $"duplicate-{Guid.NewGuid():N}@example.com";
        var initial = await client.PostAsJsonAsync("/api/v1/auth/register", new { name = "First Registration", email, password = ApiFixture.Password });
        var duplicate = await client.PostAsJsonAsync("/api/v1/auth/register", new { name = "Second Registration", email, password = ApiFixture.Password });
        await ApiFixture.ExpectAsync(initial, HttpStatusCode.Accepted);
        await ApiFixture.ExpectAsync(duplicate, HttpStatusCode.Accepted);
        Assert.Equal(await initial.Content.ReadAsStringAsync(), await duplicate.Content.ReadAsStringAsync());
        var rejected = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Rejected Password", email = $"rejected-{Guid.NewGuid():N}@example.com", password = "abcdefghijkl" });
        await ApiFixture.ExpectAsync(rejected, HttpStatusCode.BadRequest);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        Assert.Single(db.Users.Where(user => user.Email == email));
        Assert.Equal("First Registration", (await db.Users.SingleAsync(user => user.Email == email)).Name);
    }

    [Theory]
    [InlineData("abcdefghijklmnop")]
    [InlineData("lowercaseonly123!")]
    [InlineData("UPPERCASEONLY123!")]
    [InlineData("MissingDigitsOnly!")]
    [InlineData("MissingSymbolsOnly123")]
    public async Task Registration_rejects_each_missing_password_category_with_actionable_problem_details(string password)
    {
        var email = $"password-policy-{Guid.NewGuid():N}@example.com";
        using var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { name = "Password Policy", email, password });
        await ApiFixture.ExpectAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.NotEmpty(problem.GetProperty("errors").GetProperty("password").EnumerateArray());
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByEmailAsync(email));
    }

    [Fact]
    public async Task Concurrent_duplicate_registration_is_enumeration_safe_and_creates_only_one_customer()
    {
        var email = $"concurrent-{Guid.NewGuid():N}@example.com";
        using var client = fixture.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync("/api/v1/auth/register", new { name = "Concurrent Customer", email, password = ApiFixture.Password })));
        var bodies = new List<string>();
        foreach (var response in responses)
        {
            await ApiFixture.ExpectAsync(response, HttpStatusCode.Accepted);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        var user = await db.Users.SingleAsync(user => user.Email == email);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal("Customer", Assert.Single(await users.GetRolesAsync(user)));
        var tokens = await fixture.LoginAsync(email);
        using var authenticated = fixture.CreateClient(tokens.AccessToken);
        await ApiFixture.ExpectAsync(await authenticated.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_account_even_when_correct_password_is_then_supplied()
    {
        var (email, tokens) = await fixture.RegisterAsync();
        using var client = fixture.CreateClient();
        for (var attempt = 0; attempt < 5; attempt++)
            await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email, password = "Incorrect!Password123" }), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = ApiFixture.Password }), HttpStatusCode.Unauthorized);
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByEmailAsync(email))!));
        using var lockedClient = fixture.CreateClient(tokens.AccessToken);
        await ApiFixture.ExpectAsync(await lockedClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { tokens.RefreshToken }), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_current_access_and_refresh_tokens_and_still_allows_a_fresh_login()
    {
        var (email, tokens) = await fixture.RegisterAsync();
        using var client = fixture.CreateClient(tokens.AccessToken);
        await ApiFixture.ExpectAsync(await client.GetAsync($"/api/v1/stores/{ApiFixture.BashSlug}/menu-items"), HttpStatusCode.OK);
        await ApiFixture.ExpectAsync(await client.PostAsync("/api/v1/auth/logout", null), HttpStatusCode.NoContent);
        await ApiFixture.ExpectAsync(await client.GetAsync($"/api/v1/stores/{ApiFixture.BashSlug}/menu-items"), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        using var anonymous = fixture.CreateClient();
        await ApiFixture.ExpectAsync(await anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { tokens.RefreshToken }), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await anonymous.PostAsync("/api/v1/auth/logout", null), HttpStatusCode.Unauthorized);
        using var reauthenticated = fixture.CreateClient((await fixture.LoginAsync(email)).AccessToken);
        await ApiFixture.ExpectAsync(await reauthenticated.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Security_stamp_revocation_rejects_existing_access_and_refresh_tokens()
    {
        var (email, tokens) = await fixture.RegisterAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(email))!)).Succeeded);
        }
        using var client = fixture.CreateClient(tokens.AccessToken);
        await ApiFixture.ExpectAsync(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { tokens.RefreshToken }), HttpStatusCode.Unauthorized);
    }
}

public sealed class ExternalLoginCodeTests
{
    [Fact]
    public void A_restarted_ledger_cannot_redeem_codes_from_the_previous_instance()
    {
        var protection = new EphemeralDataProtectionProvider();
        var previous = new ExternalLoginCodes(protection, TimeProvider.System);
        var code = previous.Issue("user", "stamp");
        Assert.NotNull(previous.TryConsume(code));
        var restarted = new ExternalLoginCodes(protection, TimeProvider.System);
        Assert.Null(restarted.TryConsume(code));
    }

    [Fact]
    public void UserInfo_maps_verified_email_even_when_the_id_token_has_no_verification_claim()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:External:ReturnUrls:0"] = "https://client.example.com/signin",
            ["Authentication:External:Providers:test:Authority"] = "https://identity.example.com",
            ["Authentication:External:Providers:test:ClientId"] = "test-client",
            ["Authentication:External:Providers:test:ClientSecret"] = "test-only"
        }).Build();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddDataProtection();
        registrations.AddExternalLogins(configuration);
        using var services = registrations.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("test");
        Assert.True(options.GetClaimsFromUserInfoEndpoint);
        using var userInfo = JsonDocument.Parse("""{"email":"verified@example.com","email_verified":true}""");
        var identity = new ClaimsIdentity();
        foreach (var action in options.ClaimActions) action.Run(userInfo.RootElement, identity, "test");
        Assert.Equal("true", identity.FindFirst("email_verified")?.Value, ignoreCase: true);
    }

    [Fact]
    public async Task Codes_are_atomic_non_evicting_and_fail_closed_at_capacity_until_expiry()
    {
        var clock = new CodeClock();
        var codes = new ExternalLoginCodes(new EphemeralDataProtectionProvider(), clock);
        var first = codes.Issue("user", "stamp");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => codes.TryConsume(first))));
        Assert.Single(results.OfType<ExternalLoginCodes.Login>());
        for (var index = 1; index < 10_000; index++)
            Assert.NotNull(codes.TryConsume(codes.Issue("user", "stamp")));
        var overflow = codes.Issue("user", "stamp");
        Assert.Null(codes.TryConsume(overflow));
        Assert.Null(codes.TryConsume(first));
        clock.Now += ExternalLoginCodes.Lifetime;
        Assert.Null(codes.TryConsume(overflow));
        Assert.NotNull(codes.TryConsume(codes.Issue("user", "new-stamp")));
        Assert.Null(codes.TryConsume("invalid"));
    }

    private sealed class CodeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
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
        // A spoofed forwarded header must not buy a fresh allowance: only the connection address counts.
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.42");
        var limited = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "unknown@example.com", password = ApiFixture.Password });
        await ApiFixture.ExpectAsync(limited, HttpStatusCode.TooManyRequests);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.True(limited.Headers.RetryAfter!.Delta > TimeSpan.Zero ||
            limited.Headers.RetryAfter.Date > DateTimeOffset.UtcNow);
    }
}
