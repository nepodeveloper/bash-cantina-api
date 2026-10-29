using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Cantina.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Cantina.IntegrationTests;

public sealed class GlobalConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saturated_outer_limiter_blocks_downstream_work_but_not_health_probes(bool probe)
    {
        using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 128, QueueLimit = 0 });
        using var occupied = limiter.AttemptAcquire(128);
        var downstreamCalls = 0;
        var middleware = new GlobalConcurrencyMiddleware(_ =>
        {
            downstreamCalls++;
            return Task.CompletedTask;
        }, limiter);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        if (probe) context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new DisableRateLimitingAttribute()), "health"));
        await middleware.InvokeAsync(context);
        Assert.Equal(probe ? 1 : 0, downstreamCalls);
        Assert.Equal(probe ? 200 : 429, context.Response.StatusCode);
        if (!probe) Assert.Equal("1", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Outer_permit_is_released_when_downstream_authentication_fails()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var middleware = new GlobalConcurrencyMiddleware(_ => throw new InvalidOperationException("authentication"), limiter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(new DefaultHttpContext()));
        using var available = limiter.AttemptAcquire();
        Assert.True(available.IsAcquired);
    }
}

[Collection("api")]
public sealed class ApiContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task Supported_versions_are_reported_and_unknown_versions_are_not_served()
    {
        using var client = await fixture.CustomerAsync();
        var response = await client.GetAsync("/api/v1/stores");
        await ApiFixture.ExpectAsync(response, HttpStatusCode.OK);
        Assert.True(response.Headers.TryGetValues("api-supported-versions", out var supported));
        Assert.Contains("1.0", supported!);
        await ApiFixture.ExpectAsync(await client.GetAsync("/api/v2/stores"), HttpStatusCode.NotFound);
        await ApiFixture.ExpectAsync(await client.GetAsync("/api/stores"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Openapi_document_publishes_concrete_v1_paths_without_the_version_route_token()
    {
        using var client = fixture.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        await ApiFixture.ExpectAsync(response, HttpStatusCode.OK);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        var names = paths.EnumerateObject().Select(path => path.Name).ToList();
        Assert.Contains("/api/v1/stores", names);
        Assert.Contains("/api/v1/auth/logout", names);
        Assert.Contains($"/api/v1/stores/{{storeSlug}}/menu-items", names);
        Assert.Contains($"/api/v1/stores/{{storeSlug}}/menu-items/{{itemId}}/ratings", names);
        Assert.DoesNotContain(names, path => path.Contains("{version}", StringComparison.Ordinal));
        var parameters = paths.GetProperty("/api/v1/stores/{storeSlug}/menu-items")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString()).ToList();
        Assert.Contains("storeSlug", parameters);
    }

    [Fact]
    public async Task Health_probes_report_liveness_and_database_readiness()
    {
        using var client = fixture.CreateClient();
        foreach (var probe in new[] { "/health/live", "/health/ready" })
        {
            var response = await client.GetAsync(probe);
            await ApiFixture.ExpectAsync(response, HttpStatusCode.OK);
            Assert.Equal("healthy",
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }
    }
}

[Collection("catalogue-rate-limit")]
public sealed class CatalogueRateLimitTests(CatalogueRateLimitFixture fixture)
{
    [Fact]
    public async Task Catalogue_limits_are_charged_to_the_account_rather_than_the_shared_address()
    {
        using var first = await fixture.CustomerAsync();
        using var second = await fixture.CustomerAsync();
        HttpResponseMessage? limited = null;
        for (var attempt = 0; attempt < 10 && limited is null; attempt++)
        {
            var response = await first.GetAsync("/api/v1/stores");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) limited = response;
        }
        Assert.NotNull(limited);
        Assert.NotNull(limited!.Headers.RetryAfter);
        await ApiFixture.ExpectAsync(await second.GetAsync("/api/v1/stores"), HttpStatusCode.OK);
    }
}
