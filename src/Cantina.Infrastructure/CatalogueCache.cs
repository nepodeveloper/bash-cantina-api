using Microsoft.Extensions.Caching.Memory;

namespace Cantina.Infrastructure;

public sealed class CatalogueCache : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly object[] gates = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();
    private readonly long[] generations = new long[256];
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private static int Stripe(Guid storeId) => (int)((uint)storeId.GetHashCode() % 256);

    public bool TryGet<T>(Guid storeId, object key, out T? value, out long generation)
    {
        var stripe = Stripe(storeId);
        lock (gates[stripe])
        {
            generation = generations[stripe];
            return cache.TryGetValue((storeId, generation, key), out value);
        }
    }

    public void SetIfCurrent<T>(Guid storeId, object key, long generation, T value)
    {
        var stripe = Stripe(storeId);
        lock (gates[stripe])
        {
            if (generation == generations[stripe])
                cache.Set((storeId, generation, key), value,
                    new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 });
        }
    }

    public void Invalidate(Guid storeId)
    {
        var stripe = Stripe(storeId);
        lock (gates[stripe])
        {
            // Fixed stripes bound invalidation metadata; an overlapping load cannot repopulate stale results.
            generations[stripe]++;
        }
    }

    public void Dispose() => cache.Dispose();
}
