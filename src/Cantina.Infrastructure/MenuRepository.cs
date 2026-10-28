using System.Linq.Expressions;
using Cantina.Application;
using Cantina.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cantina.Infrastructure;

public sealed class MenuRepository(CantinaDbContext db, CatalogueCache cache, TimeProvider clock) : IMenuRepository
{
    private static readonly Expression<Func<MenuItem, MenuItemDto>> Projection = item =>
        new MenuItemDto(item.Id, item.StoreId, item.Kind, item.Name, item.Description, item.Price,
            item.ImageUrl, item.Ratings.Average(rating => (double?)rating.Score), item.Ratings.Count);

    public async Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken ct) =>
        await db.Stores.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new StoreDto(x.Id, x.Slug, x.Name)).ToListAsync(ct);

    public async Task<Guid?> ResolveStoreAsync(string slug, CancellationToken ct) =>
        await db.Stores.AsNoTracking().Where(x => x.Slug == slug && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);

    public Task<bool> HasMembershipAsync(Guid storeId, string userId, CancellationToken ct) =>
        db.StoreMemberships.AsNoTracking().AnyAsync(x => x.StoreId == storeId && x.UserId == userId, ct);

    public async Task<Page<MenuItemDto>> SearchAsync(Guid storeId, CatalogueQuery query, CancellationToken ct)
    {
        CatalogueValidation.Validate(query);
        ct.ThrowIfCancellationRequested();
        query = query with { Search = query.Search?.Trim() };
        if (cache.TryGet<Page<MenuItemDto>>(storeId, query, out var cached, out var generation))
            return cached!;

        var items = db.MenuItems.AsNoTracking().Where(x => x.StoreId == storeId);
        if (!string.IsNullOrEmpty(query.Search))
        {
            var pattern = "%" + query.Search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            items = items.Where(x => EF.Functions.ILike(x.Name, pattern, "\\") ||
                EF.Functions.ILike(x.Description, pattern, "\\"));
        }
        if (query.Kind.HasValue) items = items.Where(x => x.Kind == query.Kind.Value);
        if (query.MinPrice.HasValue) items = items.Where(x => x.Price >= query.MinPrice.Value);
        if (query.MaxPrice.HasValue) items = items.Where(x => x.Price <= query.MaxPrice.Value);

        var total = await items.CountAsync(ct);
        var sorted = query.Sort switch
        {
            "-name" => items.OrderByDescending(x => x.Name),
            "price" => items.OrderBy(x => x.Price),
            "-price" => items.OrderByDescending(x => x.Price),
            "rating" => items.OrderByDescending(x => x.Ratings.Average(rating => (double?)rating.Score) ?? 0),
            _ => items.OrderBy(x => x.Name)
        };
        var result = new Page<MenuItemDto>(
            (await sorted.ThenBy(x => x.Id).Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize).Select(Projection).ToListAsync(ct)).AsReadOnly(),
            query.Page, query.PageSize, total);
        cache.SetIfCurrent(storeId, query, generation, result);
        return result;
    }

    public async Task<MenuItemDto?> GetAsync(Guid storeId, Guid itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (cache.TryGet<MenuItemDto?>(storeId, itemId, out var cached, out var generation))
            return cached;
        var result = await db.MenuItems.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Id == itemId).Select(Projection).SingleOrDefaultAsync(ct);
        cache.SetIfCurrent(storeId, itemId, generation, result);
        return result;
    }

    public async Task<Page<RatingDto>?> GetRatingsAsync(Guid storeId, Guid itemId, RatingsQuery query, CancellationToken ct)
    {
        CatalogueValidation.Validate(query);
        if (!await db.MenuItems.AsNoTracking().AnyAsync(x => x.StoreId == storeId && x.Id == itemId, ct))
            return null;
        var ratings = db.Ratings.AsNoTracking().Where(x => x.StoreId == storeId && x.MenuItemId == itemId);
        var total = await ratings.CountAsync(ct);
        // Reviewer identity is deliberately not projected; ratings are public to every authenticated reader.
        var page = await ratings.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.CustomerId)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new RatingDto(x.Score, x.ReviewText, x.CreatedAtUtc, x.UpdatedAtUtc)).ToListAsync(ct);
        return new Page<RatingDto>(page.AsReadOnly(), query.Page, query.PageSize, total);
    }

    public async Task<MenuItemDto> CreateAsync(Guid storeId, MenuItemInput input, CancellationToken ct)
    {
        CatalogueValidation.Validate(input);
        var now = clock.GetUtcNow();
        var item = new MenuItem
        {
            Id = Guid.NewGuid(), StoreId = storeId, Kind = input.Kind, Name = input.Name,
            Description = input.Description, Price = input.Price, ImageUrl = input.ImageUrl,
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.MenuItems.Add(item);
        await db.SaveChangesAsync(ct);
        cache.Invalidate(storeId);
        return new(item.Id, storeId, item.Kind, item.Name, item.Description, item.Price, item.ImageUrl, null, 0);
    }

    public async Task<MenuItemDto?> UpdateAsync(Guid storeId, Guid itemId, MenuItemInput input, CancellationToken ct)
    {
        CatalogueValidation.Validate(input);
        // Tracked load plus SaveChanges so the xmin concurrency token is enforced; ExecuteUpdate would bypass it.
        var entity = await db.MenuItems.SingleOrDefaultAsync(x => x.StoreId == storeId && x.Id == itemId, ct);
        if (entity is null) return null;
        entity.Kind = input.Kind;
        entity.Name = input.Name;
        entity.Description = input.Description;
        entity.Price = input.Price;
        entity.ImageUrl = input.ImageUrl;
        entity.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        db.Entry(entity).State = EntityState.Detached;
        cache.Invalidate(storeId);
        return await GetAsync(storeId, itemId, ct);
    }

    public async Task<bool> DeleteAsync(Guid storeId, Guid itemId, CancellationToken ct)
    {
        var deleted = await db.MenuItems.Where(x => x.StoreId == storeId && x.Id == itemId).ExecuteDeleteAsync(ct);
        if (deleted > 0) cache.Invalidate(storeId);
        return deleted > 0;
    }

    public async Task<bool> RateAsync(Guid storeId, Guid itemId, string customerId, RatingInput input, CancellationToken ct)
    {
        input = CatalogueValidation.Validate(input);
        var now = clock.GetUtcNow();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Ratings" ("StoreId", "MenuItemId", "CustomerId", "Score", "ReviewText", "CreatedAtUtc", "UpdatedAtUtc")
            SELECT "StoreId", "Id", {customerId}, {input.Score}, {input.ReviewText}, {now}, {now}
            FROM "MenuItems" WHERE "StoreId" = {storeId} AND "Id" = {itemId}
            ON CONFLICT ("StoreId", "MenuItemId", "CustomerId")
            DO UPDATE SET "Score" = EXCLUDED."Score", "ReviewText" = EXCLUDED."ReviewText",
                          "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
            """, ct);
        if (affected > 0) cache.Invalidate(storeId);
        return affected > 0;
    }
}
