using Cantina.Domain;

namespace Cantina.Application;

public sealed record MenuItemInput(MenuItemKind Kind, string Name, string Description, decimal Price, string ImageUrl);
public sealed record MenuItemDto(Guid Id, Guid StoreId, MenuItemKind Kind, string Name,
    string Description, decimal Price, string ImageUrl, double? AverageRating, int RatingCount);
public sealed record StoreDto(Guid Id, string Slug, string Name);
public sealed record RatingDto(int Score, string? ReviewText, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount);
public sealed record CatalogueQuery(string? Search = null, MenuItemKind? Kind = null,
    decimal? MinPrice = null, decimal? MaxPrice = null, string Sort = "name", int Page = 1, int PageSize = 20);
public sealed record RatingsQuery(int Page = 1, int PageSize = 20);
public sealed record RatingInput(int Score, string? ReviewText = null);

public interface IMenuRepository
{
    Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken ct);
    Task<Guid?> ResolveStoreAsync(string slug, CancellationToken ct);
    Task<bool> HasMembershipAsync(Guid storeId, string userId, CancellationToken ct);
    Task<Page<MenuItemDto>> SearchAsync(Guid storeId, CatalogueQuery query, CancellationToken ct);
    Task<MenuItemDto?> GetAsync(Guid storeId, Guid itemId, CancellationToken ct);
    Task<Page<RatingDto>?> GetRatingsAsync(Guid storeId, Guid itemId, RatingsQuery query, CancellationToken ct);
    Task<MenuItemDto> CreateAsync(Guid storeId, MenuItemInput input, CancellationToken ct);
    Task<MenuItemDto?> UpdateAsync(Guid storeId, Guid itemId, MenuItemInput input, CancellationToken ct);
    Task<bool> DeleteAsync(Guid storeId, Guid itemId, CancellationToken ct);
    Task<bool> RateAsync(Guid storeId, Guid itemId, string customerId, RatingInput input, CancellationToken ct);
}

public sealed class RequestValidationException(Dictionary<string, string[]> errors) : Exception("Request validation failed.")
{
    public Dictionary<string, string[]> Errors { get; } = errors;
}

public static class CatalogueValidation
{
    public static void Validate(MenuItemInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(input.Kind)) errors["kind"] = ["Choose Dish (1) or Drink (2)."];
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 120)
            errors["name"] = ["Name is required and must be at most 120 characters."];
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 2000)
            errors["description"] = ["Description is required and must be at most 2000 characters."];
        if (input.Price < 0 || input.Price > 99999999.99m || decimal.Round(input.Price, 2) != input.Price)
            errors["price"] = ["Price must be between 0 and 99999999.99 with at most two decimal places."];
        if (string.IsNullOrWhiteSpace(input.ImageUrl) || input.ImageUrl.Length > 2048 ||
            !Uri.TryCreate(input.ImageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            errors["imageUrl"] = ["An absolute HTTPS image URL of at most 2048 characters is required."];
        ThrowIfInvalid(errors);
    }

    public static void Validate(CatalogueQuery query)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1 || query.Page > 1000000) errors["page"] = ["Page must be between 1 and 1000000."];
        if (query.PageSize is < 1 or > 100) errors["pageSize"] = ["Page size must be between 1 and 100."];
        if (query.Search?.Length > 120) errors["search"] = ["Search must be at most 120 characters."];
        if (query.Kind.HasValue && !Enum.IsDefined(query.Kind.Value)) errors["kind"] = ["Invalid item kind."];
        if (query.MinPrice < 0 || query.MaxPrice < 0 || query.MinPrice > query.MaxPrice)
            errors["price"] = ["Price bounds must be non-negative and minimum must not exceed maximum."];
        if (query.Sort is not ("name" or "-name" or "price" or "-price" or "rating"))
            errors["sort"] = ["Choose name, -name, price, -price, or rating."];
        ThrowIfInvalid(errors);
    }

    public static RatingInput Validate(RatingInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.Score is < 1 or > 5) errors["score"] = ["Score must be between 1 and 5."];
        var review = input.ReviewText?.Trim();
        if (review?.Length > 1000) errors["reviewText"] = ["Review text must be at most 1000 characters."];
        ThrowIfInvalid(errors);
        return input with { ReviewText = string.IsNullOrEmpty(review) ? null : review };
    }

    public static void Validate(RatingsQuery query)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1 || query.Page > 1000000) errors["page"] = ["Page must be between 1 and 1000000."];
        if (query.PageSize is < 1 or > 100) errors["pageSize"] = ["Page size must be between 1 and 100."];
        ThrowIfInvalid(errors);
    }

    private static void ThrowIfInvalid(Dictionary<string, string[]> errors)
    {
        if (errors.Count != 0) throw new RequestValidationException(errors);
    }
}

public sealed class MenuService(IMenuRepository repository)
{
    public Task<Page<MenuItemDto>> SearchAsync(Guid storeId, CatalogueQuery query, CancellationToken ct)
    {
        CatalogueValidation.Validate(query);
        return repository.SearchAsync(storeId, query with { Search = query.Search?.Trim() }, ct);
    }

    public Task<MenuItemDto> CreateAsync(Guid storeId, MenuItemInput input, CancellationToken ct)
    {
        CatalogueValidation.Validate(input);
        return repository.CreateAsync(storeId, input with { Name = input.Name.Trim(), Description = input.Description.Trim() }, ct);
    }

    public Task<MenuItemDto?> UpdateAsync(Guid storeId, Guid itemId, MenuItemInput input, CancellationToken ct)
    {
        CatalogueValidation.Validate(input);
        return repository.UpdateAsync(storeId, itemId, input with { Name = input.Name.Trim(), Description = input.Description.Trim() }, ct);
    }

    public Task<bool> RateAsync(Guid storeId, Guid itemId, string customerId, RatingInput input, CancellationToken ct) =>
        repository.RateAsync(storeId, itemId, customerId, CatalogueValidation.Validate(input), ct);

    public Task<Page<RatingDto>?> GetRatingsAsync(Guid storeId, Guid itemId, RatingsQuery query, CancellationToken ct)
    {
        CatalogueValidation.Validate(query);
        return repository.GetRatingsAsync(storeId, itemId, query, ct);
    }
}
