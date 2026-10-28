# ADR 0007: Optimistic concurrency on menu items via `xmin`

**Status:** Accepted — 2026-10-28

## Context

`UpdateAsync` used `ExecuteUpdateAsync`, which issues a blind `UPDATE ... WHERE id = ...`.
Two managers editing the same item concurrently produced a silent last-write-wins
overwrite, with no signal to either caller.

## Decision

Map PostgreSQL's hidden `xmin` system column as a read-only concurrency token on
`MenuItem`, and change the update path to a tracked load plus `SaveChangesAsync` so EF
includes the token in the `WHERE` clause. A lost update raises
`DbUpdateConcurrencyException`, which the error middleware translates to `409` with
ProblemDetails.

## Consequences

- Concurrent edits are reported instead of silently discarded; the client re-reads and retries.
- `xmin` is supplied by PostgreSQL on every table, so no column is added. The scaffolded
  `AddColumn`/`DropColumn` statements were removed from the migration by hand — PostgreSQL
  rejects a user column named `xmin`.
- Updates cost one extra `SELECT` by primary key. This is a write-path cost only; the
  read benchmark is unaffected.
- Deletes keep using `ExecuteDeleteAsync`. A delete that loses a race simply finds no row
  and returns `404`, which is the correct answer either way.
- Ratings are unaffected: they use an atomic `INSERT ... ON CONFLICT DO UPDATE`, where
  last-write-wins per customer is the intended behaviour.
