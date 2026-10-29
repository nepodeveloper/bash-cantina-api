using Cantina.Application;
using Cantina.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cantina.Infrastructure;

public sealed class ReviewDashboard(CantinaDbContext db) : IReviewDashboard
{
    public async Task<Page<ReviewDto>> SearchAsync(Guid storeId, ReviewQuery query, CancellationToken ct)
    {
        var filtered = Filter(storeId, query);
        var total = await filtered.CountAsync(ct);

        var joined = from rating in filtered
                     join item in db.MenuItems
                         on new { rating.StoreId, Id = rating.MenuItemId } equals new { item.StoreId, item.Id }
                     join user in db.Users on rating.CustomerId equals user.Id
                     select new { rating, item, user };
        var ordered = query.Sort switch
        {
            "updated" => joined.OrderBy(x => x.rating.UpdatedAtUtc),
            "created" => joined.OrderBy(x => x.rating.CreatedAtUtc),
            "-created" => joined.OrderByDescending(x => x.rating.CreatedAtUtc),
            "score" => joined.OrderBy(x => x.rating.Score),
            "-score" => joined.OrderByDescending(x => x.rating.Score),
            _ => joined.OrderByDescending(x => x.rating.UpdatedAtUtc)
        };
        var items = await ordered.ThenBy(x => x.rating.MenuItemId).ThenBy(x => x.rating.CustomerId)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new ReviewDto(x.item.Id, x.item.Name, x.item.Kind,
                x.user.Id, x.user.Name, x.user.Email ?? "",
                x.rating.Score, x.rating.ReviewText, x.rating.CreatedAtUtc, x.rating.UpdatedAtUtc))
            .ToListAsync(ct);
        return new Page<ReviewDto>(items.AsReadOnly(), query.Page, query.PageSize, total);
    }

    public async Task<ReviewSummary> SummariseAsync(Guid storeId, ReviewQuery query, CancellationToken ct)
    {
        var filtered = Filter(storeId, query);
        var total = await filtered.CountAsync(ct);
        if (total == 0)
            return new ReviewSummary(0, 0, null, EmptyDistribution(), [], []);

        var average = await filtered.AverageAsync(x => (double?)x.Score, ct);
        var reviewers = await filtered.Select(x => x.CustomerId).Distinct().CountAsync(ct);
        var counted = await filtered.GroupBy(x => x.Score)
            .Select(group => new ScoreBucket(group.Key, group.Count())).ToListAsync(ct);

        // Bulk review campaigns show up as a handful of accounts holding a large share of the reviews.
        var mostActive = await (from rating in filtered
                                join user in db.Users on rating.CustomerId equals user.Id
                                group rating by new { user.Id, user.Name, user.Email } into grouped
                                orderby grouped.Count() descending, grouped.Key.Id
                                select new ReviewerActivity(grouped.Key.Id, grouped.Key.Name, grouped.Key.Email ?? "",
                                    grouped.Count(), grouped.Average(x => (double)x.Score),
                                    grouped.Min(x => x.CreatedAtUtc), grouped.Max(x => x.UpdatedAtUtc)))
            .Take(10).ToListAsync(ct);

        var lowestRated = await (from rating in filtered
                                 join item in db.MenuItems
                                     on new { rating.StoreId, Id = rating.MenuItemId } equals new { item.StoreId, item.Id }
                                 group rating by new { item.Id, item.Name } into grouped
                                 orderby grouped.Average(x => (double)x.Score), grouped.Key.Name
                                 select new ItemRating(grouped.Key.Id, grouped.Key.Name,
                                     grouped.Average(x => (double)x.Score), grouped.Count()))
            .Take(10).ToListAsync(ct);

        var distribution = EmptyDistribution()
            .Select(bucket => counted.FirstOrDefault(x => x.Score == bucket.Score) ?? bucket).ToList();
        return new ReviewSummary(total, reviewers, average, distribution.AsReadOnly(),
            mostActive.AsReadOnly(), lowestRated.AsReadOnly());
    }

    private IQueryable<Rating> Filter(Guid storeId, ReviewQuery query)
    {
        var ratings = db.Ratings.AsNoTracking().Where(x => x.StoreId == storeId);
        if (query.MenuItemId is { } itemId) ratings = ratings.Where(x => x.MenuItemId == itemId);
        if (query.MinScore is { } min) ratings = ratings.Where(x => x.Score >= min);
        if (query.MaxScore is { } max) ratings = ratings.Where(x => x.Score <= max);
        if (query.FromUtc is { } from) ratings = ratings.Where(x => x.UpdatedAtUtc >= from);
        if (query.ToUtc is { } to) ratings = ratings.Where(x => x.UpdatedAtUtc <= to);
        if (query.WithTextOnly) ratings = ratings.Where(x => x.ReviewText != null);
        if (!string.IsNullOrEmpty(query.Search))
        {
            var pattern = "%" + query.Search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            ratings = ratings.Where(x => x.ReviewText != null &&
                EF.Functions.Like(x.ReviewText.ToLower(), pattern.ToLower(), "\\"));
        }
        return ratings;
    }

    private static List<ScoreBucket> EmptyDistribution() =>
        [.. Enumerable.Range(1, 5).Select(score => new ScoreBucket(score, 0))];
}
