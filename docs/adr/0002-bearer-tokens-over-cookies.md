# ADR 0002: Identity bearer tokens instead of a shared session cookie

**Status:** Accepted — 2026-10-28

## Context

The original design sketch proposed a same-origin `Path=/` authentication cookie so one
login would work across `/bash` and `/sportscene`. Cookies would require antiforgery
tokens on every state-changing route, origin checks, and a browser-shaped client. The
deliverable is a JSON API with no frontend, exercised from HTTP clients and tests.

## Decision

Use ASP.NET Core Identity **opaque bearer tokens** (`IdentityConstants.BearerScheme`).
Access tokens last 15 minutes, refresh tokens 7 days. Every authenticated request
re-validates the security stamp, `CanSignIn`, and lockout state.

## Consequences

- No CSRF surface: there is no ambient credential for a browser to attach automatically,
  so antiforgery tokens are not required.
- Tokens are **not JWTs** and cannot be decoded by clients. Any future external API
  consumer needs a standards-compliant OIDC provider, not these tokens.
- One account still works across both stores, because identity is global and only
  catalogue data and administrative rights are store-scoped.
- Revocation is stamp-based: `POST /api/v1/auth/logout` rotates the security stamp and
  invalidates every token already issued to that account.
- Password reset, email verification and MFA are out of scope and not implemented.
