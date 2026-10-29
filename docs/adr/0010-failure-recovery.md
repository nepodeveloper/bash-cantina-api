# ADR 0010: Failure recovery — retry, replay and rollback

**Status:** Accepted — 2026-10-28

## Context

"If something goes wrong mid-request, can we re-run it, replay it, or roll it back?" Each of those
is a different failure and needs a different mechanism. Previously the API had none of them
explicitly: a dropped connection mid-write left the client unable to tell whether the write
happened, and a retry risked creating a duplicate menu item.

## Decision

Four layers, each owning one failure mode.

### 1. Rollback — atomic units of work

Creating a customer is several Identity writes (user, role, optional external login). These now run
inside one transaction in `CustomerProvisioner`, so the outcome is all or nothing. The previous
code compensated by deleting the user after a failed role assignment, which is not atomic and
leaves a window where a user exists with no role.

Single-statement writes are already atomic. The rating upsert is one `INSERT ... ON CONFLICT`, and
menu-item delete is one `DELETE`.

### 2. Retry — transient faults, invisible to the caller

Npgsql's `EnableRetryOnFailure` retries transient network and failover errors in place. Because a
retrying execution strategy refuses to silently re-run a user-managed transaction, every explicit
transaction is wrapped in `CreateExecutionStrategy().ExecuteAsync(...)` and clears the change
tracker on entry so each attempt starts clean.

### 3. Replay — client-driven, safe retry of a non-idempotent write

`POST /menu-items` accepts an `Idempotency-Key` header. The store implements a reservation protocol:

| Situation | Result |
|---|---|
| Key unseen | Reserved; the write proceeds and its response is captured |
| Same key, same body, finished | `200/201` replayed verbatim with `Idempotency-Replayed: true` |
| Same key, same body, still running | `409` with `Retry-After` |
| Same key, different body | `422` |
| Request failed before commit | Business write rolled back and reservation released |
| Delivery failed after commit | Completed response retained for replay |

Reservation acquisition is an atomic primary-key upsert with a unique owner token. Completed
keys expire after 24 hours, checked during acquisition rather than only during unrelated cleanup.
Uncompleted reservations can be reclaimed after a two-minute lease. Completion and release
require the current owner, so an abandoned caller cannot affect a replacement reservation.

The business write and completed response are committed in one transaction under a reservation
row lock. This prevents a second owner from taking over while a write is actively committing.
The response is buffered, including its `Location` header, and delivered only after commit.
A disconnect after commit never releases the completed record. Catalogue cache invalidation
also runs after commit.

`PUT` and `DELETE` are already idempotent by HTTP semantics and need no key.

### 4. Diagnosis — correlating what actually happened

Every response carries `X-Correlation-ID`, echoed into the ProblemDetails body and attached to the
structured log scope, so a failed call can be traced to its log line.

## Consequences

- A client that times out can safely retry a create with the same key and will either get the
  original response or a clear "still running" signal. It never silently double-creates.
- Idempotency records expire after 24 hours and expired records are cleaned up per user during
  acquisition, without a scheduled job or a full-table scan. Inactive users' records are removed
  on their next keyed request.
- `409` now has two meanings: a lost update (ADR 0007) and an in-flight idempotent retry. Both are
  retry-after-re-read, and the ProblemDetails title distinguishes them.
- Retry is for *transient* faults only. A failed business rule is not retried, and non-idempotent
  work is never blindly re-run — that is exactly what the idempotency key exists to make safe.

## Explicitly not done

- **No request/response body audit log.** Storing bodies verbatim to "replay traffic later" would
  persist credentials and customer PII. Idempotent replay is scoped to the one caller that owns the
  key, for a bounded window.
- **No outbox or saga.** There is a single database and no external side effects to coordinate. An
  outbox would be the right answer the moment the API starts publishing events or calling payments.
- **No automatic server-side replay of failed requests.** Retry is the client's decision; the API's
  job is to make that retry safe.
