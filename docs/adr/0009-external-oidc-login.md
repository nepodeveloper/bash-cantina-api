# ADR 0009: OAuth 2.0 / OIDC sign-in via a one-time code

**Status:** Accepted — 2026-10-28

## Context

The API issues opaque Identity bearer tokens ([ADR 0002](0002-bearer-tokens-over-cookies.md)).
Federated sign-in is inherently redirect-based, so the handshake has to end by handing a credential
back to a client over a browser redirect. Putting tokens in the redirect URL is the implicit-flow
mistake: URLs leak into browser history, `Referer` headers, and proxy and server logs.

## Decision

Authorization Code flow with PKCE against any OpenID Connect provider, configured by name:

```text
Authentication:External:Providers:<scheme>:{DisplayName,Authority,ClientId,ClientSecret}
Authentication:External:ReturnUrls:[]
```

The callback resolves the local account and redirects to an **allow-listed** return URL carrying a
short-lived, single-use, data-protected `code`. The client exchanges it at
`POST /auth/external/exchange` for exactly the same token pair that password login issues.

## Consequences

- Open/closed: adding Google, Entra ID or Keycloak is configuration only — no new code, no new
  scheme constants. When nothing is configured the handlers are never registered and the start
  endpoint returns `404`.
- No tokens in URLs. The code lives for two minutes, is bound to a data-protection purpose, and is
  rejected on second use.
- Return URLs are matched exactly against the allow list, so the callback cannot be turned into an
  open redirect that leaks the code.
- **Accounts are linked only on a provider-asserted verified email.** Linking on an unverified
  address would let anyone who can sign up at a provider claim an existing Cantina account.
- One identity either way: an externally provisioned user is an ordinary `Customer` with the same
  ratings, store memberships, lockout and security-stamp revocation as a password user. Logout and
  `/auth/exchange` both honour `CanSignIn` and lockout.
- Single-use enforcement is process-local, consistent with the single-instance assumption in
  [ADR 0004](0004-process-local-catalogue-cache.md). Multiple replicas need a shared store for the
  consumed-code set, or sticky routing, before this guarantee holds cluster-wide.
- The external cookie is only the in-flight handshake. No long-lived cookie session is created, so
  the CSRF posture from ADR 0002 is unchanged.
