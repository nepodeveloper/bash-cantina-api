# ADR 0005: PostgreSQL everywhere, including tests

**Status:** Accepted — 2026-10-28 (supersedes the optional EF Core in-memory provider)

## Context

`AddInfrastructure` previously honoured `Database:Provider=InMemory` and the repository
carried a parallel code path for non-relational providers. Nothing used it: integration
tests run Testcontainers PostgreSQL and the benchmarks use Npgsql. The in-memory provider
enforces no check constraints, no unique indexes, no cascade behaviour and cannot execute
the `ON CONFLICT` rating upsert — so the branch that ran in tests was not the branch that
ran in production.

## Decision

Remove the in-memory provider and every `Database.IsRelational()` branch. The composition
root requires `ConnectionStrings:DefaultConnection` and always configures Npgsql.
Integration tests use real, isolated PostgreSQL containers.

## Consequences

- One data-access path, exercised exactly as deployed. Constraint violations, concurrency
  and migrations are all covered by the tests that already exist.
- `dotnet test` requires a running Docker engine. There is no silent skip: Docker-less
  runs fail loudly rather than reporting a green suite that proved nothing.
- Repository code lost roughly a third of its branches, including the duplicate
  read-modify-write rating path.
