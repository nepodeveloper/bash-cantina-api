using Cantina.Application;
using Cantina.Infrastructure;

namespace Cantina.IntegrationTests;

public sealed class CatalogueCacheTests
{
    [Fact]
    public void Identical_query_keys_cannot_return_another_stores_cached_value()
    {
        using var cache = new CatalogueCache();
        var query = new CatalogueQuery(Search: "same query");
        Assert.False(cache.TryGet<string>(ApiFixture.Bash, query, out _, out var bashGeneration));
        Assert.False(cache.TryGet<string>(ApiFixture.Sportscene, query, out _, out var sportsceneGeneration));
        cache.SetIfCurrent(ApiFixture.Bash, query, bashGeneration, "Bash result");
        cache.SetIfCurrent(ApiFixture.Sportscene, query, sportsceneGeneration, "Sportscene result");
        Assert.True(cache.TryGet<string>(ApiFixture.Bash, query, out var bash, out _));
        Assert.True(cache.TryGet<string>(ApiFixture.Sportscene, query, out var sportscene, out _));
        Assert.Equal("Bash result", bash);
        Assert.Equal("Sportscene result", sportscene);
    }

    [Fact]
    public void Invalidation_prevents_an_in_flight_stale_load_from_repopulating_cache()
    {
        using var cache = new CatalogueCache();
        var query = new CatalogueQuery();
        Assert.False(cache.TryGet<string>(ApiFixture.Bash, query, out _, out var oldGeneration));
        cache.Invalidate(ApiFixture.Bash);
        cache.SetIfCurrent(ApiFixture.Bash, query, oldGeneration, "stale result");
        Assert.False(cache.TryGet<string>(ApiFixture.Bash, query, out _, out var currentGeneration));
        Assert.NotEqual(oldGeneration, currentGeneration);
        cache.SetIfCurrent(ApiFixture.Bash, query, currentGeneration, "fresh result");
        cache.SetIfCurrent(ApiFixture.Bash, query, oldGeneration, "late stale result");
        Assert.True(cache.TryGet<string>(ApiFixture.Bash, query, out var actual, out _));
        Assert.Equal("fresh result", actual);
    }

    [Fact]
    public void Invalidation_removes_previously_cached_results_from_all_query_variants()
    {
        using var cache = new CatalogueCache();
        var first = new CatalogueQuery(Page: 1);
        var second = new CatalogueQuery(Page: 2);
        Assert.False(cache.TryGet<string>(ApiFixture.Bash, first, out _, out var generation));
        cache.SetIfCurrent(ApiFixture.Bash, first, generation, "first page");
        cache.SetIfCurrent(ApiFixture.Bash, second, generation, "second page");
        cache.Invalidate(ApiFixture.Bash);
        Assert.False(cache.TryGet<string>(ApiFixture.Bash, first, out _, out _));
        Assert.False(cache.TryGet<string>(ApiFixture.Bash, second, out _, out _));
    }
}
