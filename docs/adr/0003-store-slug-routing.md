# ADR 0003: Store slugs in routes, not store identifiers

**Status:** Accepted — 2026-10-28 (supersedes GUID-based store routes)

## Context

The first implementation addressed stores by primary key:
`/api/v1/stores/{storeId:guid}/menu-items`. `Store.Slug` existed, was unique-indexed and
returned by `GET /api/v1/stores`, but was never used for routing. GUID routes are opaque
in a demo, unreadable in logs, and do not resemble the `bash.com/sportscene` model the
multi-store design is based on.

## Decision

Address stores by slug: `/api/v1/stores/{storeSlug}/menu-items`. A group-level endpoint
filter resolves the slug to a store id once per request and publishes it through a
request-scoped `StoreContext` that handlers and the store-manager filter consume.
An unknown or inactive slug returns `404` before any handler runs.

## Consequences

- URLs are readable and stable; store ids stay an internal detail.
- One indexed lookup per store-scoped request, on `Stores.Slug` (unique index).
- Only active stores resolve, so `Store.IsActive` becomes the switch for retiring a
  storefront without deleting its catalogue.
- A previously valid GUID URL now returns `404`. This is a breaking change, taken before
  the first release so no `v2` is warranted — see [ADR 0006](0006-url-segment-versioning.md).
- Store membership and authorization continue to key on the store id, never on the slug
  supplied by the caller.
