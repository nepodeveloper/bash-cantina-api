using System.Security.Claims;
using Cantina.Application;
using Cantina.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Cantina.Api;

public static class CatalogueEndpoints
{
    public static void MapCatalogueEndpoints(this RouteGroupBuilder storeScope)
    {
        var menu = storeScope.MapGroup("/menu-items").WithTags("Catalogue");
        menu.MapGet("/", async (StoreContext store, [AsParameters] SearchParameters query,
            MenuService service, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(store.Id, new CatalogueQuery(query.Search, query.Kind,
                query.MinPrice, query.MaxPrice, query.Sort ?? "name", query.Page ?? 1, query.PageSize ?? 20), ct)));
        menu.MapGet("/{itemId:guid}", async (StoreContext store, Guid itemId, IMenuRepository repository,
            HttpContext context, CancellationToken ct) =>
            await repository.GetAsync(store.Id, itemId, ct) is { } item
                ? Results.Ok(item) : ApiErrors.Problem(context, 404));
        menu.MapGet("/{itemId:guid}/ratings", async (StoreContext store, Guid itemId, int? page, int? pageSize,
            MenuService service, HttpContext context, CancellationToken ct) =>
            await service.GetRatingsAsync(store.Id, itemId, new RatingsQuery(page ?? 1, pageSize ?? 20), ct) is { } ratings
                ? Results.Ok(ratings) : ApiErrors.Problem(context, 404));
        menu.MapPost("/", async (StoreContext store, MenuItemInput input, MenuService service, CancellationToken ct) =>
        {
            var item = await service.CreateAsync(store.Id, input, ct);
            return Results.Created($"/api/v1/stores/{store.Slug}/menu-items/{item.Id}", item);
        }).RequireAuthorization("ManageMenu").AddEndpointFilter<StoreManagerFilter>()
            .AddEndpointFilter<IdempotencyFilter<MenuItemInput>>();
        menu.MapPut("/{itemId:guid}", async (StoreContext store, Guid itemId, MenuItemInput input,
            MenuService service, HttpContext context, CancellationToken ct) =>
            await service.UpdateAsync(store.Id, itemId, input, ct) is { } item
                ? Results.Ok(item) : ApiErrors.Problem(context, 404))
            .RequireAuthorization("ManageMenu").AddEndpointFilter<StoreManagerFilter>();
        menu.MapDelete("/{itemId:guid}", async (StoreContext store, Guid itemId, IMenuRepository repository,
            HttpContext context, CancellationToken ct) =>
            await repository.DeleteAsync(store.Id, itemId, ct)
                ? Results.NoContent() : ApiErrors.Problem(context, 404))
            .RequireAuthorization("ManageMenu").AddEndpointFilter<StoreManagerFilter>();
        menu.MapPut("/{itemId:guid}/rating", async (StoreContext store, Guid itemId, RatingInput input,
            ClaimsPrincipal user, MenuService service, HttpContext context, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return ApiErrors.Problem(context, 401);
            return await service.RateAsync(store.Id, itemId, userId, input, ct)
                ? Results.NoContent() : ApiErrors.Problem(context, 404);
        }).RequireAuthorization("RateMenu");
    }

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
