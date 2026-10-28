using Cantina.Domain;

namespace Cantina.Application;

public sealed record ReviewDto(Guid MenuItemId, string MenuItemName, MenuItemKind Kind,
    string CustomerId, string CustomerName, string CustomerEmail,
    int Score, string? ReviewText, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record ScoreBucket(int Score, int Count);

public sealed record ReviewerActivity(string CustomerId, string CustomerName, string CustomerEmail,
    int ReviewCount, double AverageScore, DateTimeOffset FirstReviewUtc, DateTimeOffset LastReviewUtc);

public sealed record ItemRating(Guid MenuItemId, string Name, double AverageScore, int RatingCount);

public sealed record ReviewSummary(int TotalReviews, int DistinctReviewers, double? AverageScore,
    IReadOnlyList<ScoreBucket> Distribution, IReadOnlyList<ReviewerActivity> MostActiveReviewers,
    IReadOnlyList<ItemRating> LowestRatedItems);

public sealed record ReviewQuery(int? MinScore = null, int? MaxScore = null, Guid? MenuItemId = null,
    string? Search = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null,
    bool WithTextOnly = false, string Sort = "-updated", int Page = 1, int PageSize = 20);

/// <summary>Read-only moderation view over ratings; segregated from the catalogue write surface.</summary>
public interface IReviewDashboard
{
    Task<Page<ReviewDto>> SearchAsync(Guid storeId, ReviewQuery query, CancellationToken ct);
    Task<ReviewSummary> SummariseAsync(Guid storeId, ReviewQuery query, CancellationToken ct);
}

public static class ReviewValidation
{
    public static readonly string[] SupportedSorts = ["updated", "-updated", "created", "-created", "score", "-score"];

    public static ReviewQuery Validate(ReviewQuery query)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1 || query.Page > 1000000) errors["page"] = ["Page must be between 1 and 1000000."];
        if (query.PageSize is < 1 or > 100) errors["pageSize"] = ["Page size must be between 1 and 100."];
        if (query.MinScore is < 1 or > 5 || query.MaxScore is < 1 or > 5 || query.MinScore > query.MaxScore)
            errors["score"] = ["Score bounds must be between 1 and 5 and minimum must not exceed maximum."];
        var search = query.Search?.Trim();
        if (search?.Length > 120) errors["search"] = ["Search must be at most 120 characters."];
        if (query.FromUtc > query.ToUtc) errors["date"] = ["fromUtc must not be later than toUtc."];
        if (!SupportedSorts.Contains(query.Sort))
            errors["sort"] = [$"Choose {string.Join(", ", SupportedSorts)}."];
        if (errors.Count != 0) throw new RequestValidationException(errors);
        return query with { Search = string.IsNullOrEmpty(search) ? null : search };
    }
}

public sealed class ReviewService(IReviewDashboard dashboard)
{
    public Task<Page<ReviewDto>> SearchAsync(Guid storeId, ReviewQuery query, CancellationToken ct) =>
        dashboard.SearchAsync(storeId, ReviewValidation.Validate(query), ct);

    public Task<ReviewSummary> SummariseAsync(Guid storeId, ReviewQuery query, CancellationToken ct) =>
        dashboard.SummariseAsync(storeId, ReviewValidation.Validate(query), ct);
}
