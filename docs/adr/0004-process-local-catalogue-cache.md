# ADR 0004: Process-local catalogue cache

**Status:** Accepted — 2026-10-28

## Context

Catalogue reads dominate traffic and are identical across users: the response depends on
the store and the query string only, never on the caller. Benchmarks showed the uncached
projection at roughly 9 ms per request against 10,000 seeded items.

## Decision

Use a bounded in-process `MemoryCache` (1,024 entries, 30-second TTL) keyed by store id,
a per-store generation counter and the query. Every write — create, update, delete and
rating — increments the store generation, so cached pages are abandoned rather than
individually evicted. Generation checks stop an in-flight stale read from repopulating.

## Consequences

- Warm reads drop to microseconds with a bounded, predictable memory ceiling.
- Cache keys always contain the store id, so two stores cannot serve each other's pages.
- No user-specific data is cached, so one account can never read another's response.
- **The cache is per process.** Running more than one replica requires either a shared
  invalidation channel (Redis pub/sub), a distributed cache, or disabling the cache.
  A TTL alone does not give cross-instance consistency.
- Offset pagination does not promise a snapshot across requests; concurrent writes are
  last-write-wins except where [ADR 0007](0007-optimistic-concurrency.md) applies.
