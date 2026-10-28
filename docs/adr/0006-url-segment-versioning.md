# ADR 0006: URL-segment API versioning

**Status:** Accepted — 2026-10-28

## Context

Routes were hard-coded strings beginning `/api/v1`. That looks versioned but carries no
version metadata: clients get no `api-supported-versions` header, there is no way to mark
a version deprecated, and an unknown version falls through to generic routing.

## Decision

Use `Asp.Versioning.Http` with `UrlSegmentApiVersionReader`. Route groups are declared as
`/api/v{version:apiVersion}/...` and joined to a version set that declares `1.0` and
reports supported versions. `UnsupportedApiVersionStatusCode` is `404`. The published
OpenAPI document rewrites the `{version}` token to the concrete `v1` path so the contract
shows the URLs clients actually call.

## Consequences

- Every response carries `api-supported-versions`; deprecations can later add
  `api-deprecated-versions` and a `Sunset` header without touching handlers.
- `/api/v2/...` returns `404` with ProblemDetails rather than an accidental match.

### Compatibility policy

A change ships inside `v1` when it is additive and optional: new optional request fields,
new response fields, new endpoints, new enum members on fields documented as extensible.

A change requires `v2` when it removes or renames a field, changes a field's type or
meaning, tightens validation on existing input, changes an existing status code, or
changes the authentication contract. When `v2` is introduced, `v1` is marked deprecated,
keeps a published sunset date of at least 90 days, and both versions are covered by tests
until `v1` is withdrawn.
