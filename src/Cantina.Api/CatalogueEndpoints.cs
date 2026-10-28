using System.Security.Claims;
using Cantina.Application;
using Cantina.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Cantina.Api;

public static class CatalogueEndpoints
{
    public static void MapCatalogueEndpoints(this WebApplication app)
    {
        var stores = app.MapGroup("/api/v1/stores").RequireRateLimiting("catalogue").WithTags("Catalogue");
        stores.MapGet("/", async (IMenuRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetStoresAsync(ct)));
        var menu = stores.MapGroup("/{storeId:guid}/menu-items");
        menu.AddEndpointFilter(async (context, next) =>
        {
            var storeId = Guid.Parse(context.HttpContext.Request.RouteValues["storeId"]!.ToString()!);
            var repository = context.HttpContext.RequestServices.GetRequiredService<IMenuRepository>();
            return await repository.StoreExistsAsync(storeId, context.HttpContext.RequestAborted)
                ? await next(context) : ApiErrors.Problem(context.HttpContext, 404);
        });
        menu.MapGet("/", async (Guid storeId, [AsParameters] SearchParameters query,
            MenuService service, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(storeId, new CatalogueQuery(query.Search, query.Kind,
                query.MinPrice, query.MaxPrice, query.Sort ?? "name", query.Page ?? 1, query.PageSize ?? 20), ct)));
        menu.MapGet("/{itemId:guid}", async (Guid storeId, Guid itemId, IMenuRepository repository,
            HttpContext context, CancellationToken ct) =>
            await repository.GetAsync(storeId, itemId, ct) is { } item
                ? Results.Ok(item) : ApiErrors.Problem(context, 404));
        menu.MapPost("/", async (Guid storeId, MenuItemInput input, MenuService service, CancellationToken ct) =>
        {
            var item = await service.CreateAsync(storeId, input, ct);
            return Results.Created($"/api/v1/stores/{storeId}/menu-items/{item.Id}", item);
        }).RequireAuthorization("ManageMenu").AddEndpointFilter<StoreManagerFilter>();
        menu.MapPut("/{itemId:guid}", async (Guid storeId, Guid itemId, MenuItemInput input,
            MenuService service, HttpContext context, CancellationToken ct) =>
            await service.UpdateAsync(storeId, itemId, input, ct) is { } item
                ? Results.Ok(item) : ApiErrors.Problem(context, 404))
            .RequireAuthorization("ManageMenu").AddEndpointFilter<StoreManagerFilter>();
        menu.MapDelete("/{itemId:guid}", async (Guid storeId, Guid itemId, IMenuRepository repository,
            HttpContext context, CancellationToken ct) =>
            await repository.DeleteAsync(storeId, itemId, ct)
                ? Results.NoContent() : ApiErrors.Problem(context, 404))
            .RequireAuthorization("ManageMenu").AddEndpointFilter<StoreManagerFilter>();
        menu.MapPut("/{itemId:guid}/rating", async (Guid storeId, Guid itemId, RatingInput input,
            ClaimsPrincipal user, MenuService service, HttpContext context, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return ApiErrors.Problem(context, 401);
            return await service.RateAsync(storeId, itemId, userId, input.Score, ct)
                ? Results.NoContent() : ApiErrors.Problem(context, 404);
        }).RequireAuthorization("RateMenu");
    }

    public sealed record RatingInput(int Score);
    public sealed class SearchParameters
    {
        public string? Search { get; set; }
        public MenuItemKind? Kind { get; set; }
        public decimal? MinPrice { get; set; }
        public decimal? MaxPrice { get; set; }
        public string? Sort { get; set; }
        public int? Page { get; set; }
        public int? PageSize { get; set; }
    }
}

public sealed class StoreManagerFilter(IMenuRepository repository) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.IsInRole("Admin")) return await next(context);
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var storeId = Guid.Parse(http.Request.RouteValues["storeId"]!.ToString()!);
        return userId is not null && await repository.HasMembershipAsync(storeId, userId, http.RequestAborted)
            ? await next(context) : ApiErrors.Problem(http, 403);
    }
}
