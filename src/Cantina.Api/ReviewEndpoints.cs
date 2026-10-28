using Cantina.Application;
using Microsoft.AspNetCore.Mvc;

namespace Cantina.Api;

/// <summary>
/// Staff-only moderation view. Unlike the public per-item ratings list it discloses the reviewer,
/// because spotting one account reviewing across many items is the whole point of the dashboard.
/// </summary>
public static class ReviewEndpoints
{
    public static void MapReviewEndpoints(this RouteGroupBuilder storeScope)
    {
        var reviews = storeScope.MapGroup("/reviews").WithTags("Review dashboard")
            .RequireAuthorization("ManageMenu").AddEndpointFilter<StoreManagerFilter>();
        reviews.MapGet("/", async (StoreContext store, [AsParameters] ReviewParameters parameters,
            ReviewService service, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(store.Id, parameters.ToQuery(), ct)));
        reviews.MapGet("/summary", async (StoreContext store, [AsParameters] ReviewParameters parameters,
            ReviewService service, CancellationToken ct) =>
            Results.Ok(await service.SummariseAsync(store.Id, parameters.ToQuery(), ct)));
    }

    public sealed class ReviewParameters
    {
        public int? MinScore { get; set; }
        public int? MaxScore { get; set; }
        public Guid? MenuItemId { get; set; }
        public string? Search { get; set; }
        public DateTimeOffset? FromUtc { get; set; }
        public DateTimeOffset? ToUtc { get; set; }
        public bool? WithTextOnly { get; set; }
        public string? Sort { get; set; }
        public int? Page { get; set; }
        public int? PageSize { get; set; }

        public ReviewQuery ToQuery() => new(MinScore, MaxScore, MenuItemId, Search, FromUtc, ToUtc,
            WithTextOnly ?? false, Sort ?? "-updated", Page ?? 1, PageSize ?? 20);
    }
}
