# ADR 0001: Modular monolith over microservices

**Status:** Accepted — 2026-10-28

## Context

The brief covers one bounded context: a store-scoped menu catalogue with customer ratings
and authentication. There is no independent scaling pressure, no separate team ownership,
and no differing availability requirement between the catalogue and identity features.

## Decision

Ship one deployable API and one PostgreSQL database, split into four projects
(`Domain`, `Application`, `Infrastructure`, `Api`) with Clean Architecture dependency
rules. Feature boundaries stay explicit so a future extraction remains cheap.

## Consequences

- One build, one deployment, one migration pipeline; local setup is `docker compose up`.
- Cross-feature changes stay transactional; no distributed consistency work.
- Horizontal scaling is by stateless replicas, which requires the shared-cache change
  described in [ADR 0004](0004-process-local-catalogue-cache.md).
- If a feature later needs independent scaling or release cadence, the project split
  gives a seam to extract it; nothing in the code assumes a single process beyond the cache.
