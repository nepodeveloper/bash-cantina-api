# ADR 0008: Staff review dashboard separate from the public ratings list

**Status:** Accepted — 2026-10-28

## Context

`GET /menu-items/{id}/ratings` is deliberately anonymised and scoped to one item. That is correct
for a storefront, but it is the wrong tool for the abuse scenario in the brief: an attacker
creating many accounts and spraying one-star reviews is invisible when you can only ever look at
one item at a time with the reviewer hidden.

## Decision

Add a store-scoped, staff-only dashboard behind the `ManageMenu` policy and the store-membership
filter: `GET /stores/{slug}/reviews` for the filterable list and `GET /stores/{slug}/reviews/summary`
for aggregates. Both discover reviewer identity. The summary returns the score distribution, the
most active reviewers, and the lowest-rated items for the current filter.

It is served by its own port, `IReviewDashboard`, rather than being bolted onto `IMenuRepository`.

## Consequences

- Interface segregation: the catalogue write surface does not grow a reporting method, and a
  reporting change cannot break catalogue consumers. The dashboard is read-only by construction.
- Bulk review campaigns are visible: a handful of accounts holding a large share of the reviews,
  all created in a narrow window, stands out in `mostActiveReviewers`.
- The dashboard discloses reviewer name and email, which the public endpoint never does. It is
  restricted to a store's own staff, and store managers still cannot read another store's reviews.
- The summary is not cached. It is a low-traffic staff screen and must never show stale moderation
  data; the catalogue cache deliberately does not cover it.
- Aggregates are computed in PostgreSQL, not in memory, so the response size is bounded regardless
  of how many reviews a store has.
