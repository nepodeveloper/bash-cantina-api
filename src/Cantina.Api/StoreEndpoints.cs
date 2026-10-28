using System.Security.Claims;
using Asp.Versioning.Builder;
using Cantina.Application;

namespace Cantina.Api;

/// <summary>Store resolved from the request slug, shared by the route filters and handlers.</summary>
public sealed class StoreContext
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
}

public static class StoreEndpoints
{
    /// <summary>
    /// Maps the store collection and returns the store-scoped group. Slug resolution lives here once,
    /// so every feature mounted under it receives an already-resolved <see cref="StoreContext"/>.
    /// </summary>
    public static RouteGroupBuilder MapStoreEndpoints(this WebApplication app, ApiVersionSet versions)
    {
        var stores = app.MapGroup("/api/v{version:apiVersion}/stores").WithApiVersionSet(versions)
            .RequireRateLimiting("catalogue").RequireAuthorization();
        stores.MapGet("/", async (IMenuRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetStoresAsync(ct))).WithTags("Stores");

        var store = stores.MapGroup("/{storeSlug:maxlength(80)}");
        store.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var slug = http.Request.RouteValues["storeSlug"]?.ToString();
            var repository = http.RequestServices.GetRequiredService<IMenuRepository>();
            if (slug is null || await repository.ResolveStoreAsync(slug, http.RequestAborted) is not { } storeId)
                return ApiErrors.Problem(http, 404);
            var resolved = http.RequestServices.GetRequiredService<StoreContext>();
            resolved.Id = storeId;
            resolved.Slug = slug;
            return await next(context);
        });
        return store;
    }
}

/// <summary>Resource-based check: the role alone never grants access to a specific store.</summary>
public sealed class StoreManagerFilter(IMenuRepository repository, StoreContext store) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.IsInRole("Admin")) return await next(context);
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is not null && await repository.HasMembershipAsync(store.Id, userId, http.RequestAborted)
            ? await next(context) : ApiErrors.Problem(http, 403);
    }
}
