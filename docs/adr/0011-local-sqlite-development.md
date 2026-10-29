# ADR 0011: SQLite for local development

**Status:** Accepted — 2026-10-29

## Context

Local development previously required a PostgreSQL server, usually started through Docker.
That setup is unavailable in some environments. The API uses PostgreSQL-specific behavior
in production, so replacing it globally with an in-memory or NoSQL store would weaken
production parity and require a broader rewrite.

## Decision

Use SQLite as the default local-development provider, stored in
`src/Cantina.Api/cantina.db`. It needs no separate service or user-secret. SQLite uses
`EnsureCreated` for first-run schema creation; the checked-in EF migrations remain for
PostgreSQL. Compose, production, and Docker-backed integration tests explicitly use
PostgreSQL.

Provider-specific mappings preserve decimal prices, UTC timestamps, menu-item optimistic
concurrency, case-insensitive search, and rating/idempotency upserts on SQLite.

## Consequences

- Developers can run the API without installing Docker or PostgreSQL.
- SQLite is for a single local API instance, not multi-instance deployments.
- `EnsureCreated` does not apply schema changes to an existing SQLite file. Recreate the
  local database after model changes; this also deletes its local accounts and data.
- PostgreSQL remains the production provider and the source of truth for integration tests.