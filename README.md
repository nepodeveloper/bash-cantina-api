# Bash Cantina API

A .NET 10 / ASP.NET Core REST API for the TFG Senior Software Engineer assessment.
One modular monolith serves Bash and Sportscene, with a shared customer identity and
store-scoped dishes, drinks, staff permissions, and ratings. Password login works out of
the box; OpenID Connect single sign-on is enabled by configuration alone. A staff review
dashboard surfaces coordinated review abuse.

## Run with Docker (Windows, macOS, or Linux)

Install Docker Desktop (Linux containers), then:

```powershell
Copy-Item .env.example .env
# Edit .env: supply POSTGRES_PASSWORD.
# Optionally supply BOTH BOOTSTRAP_ADMIN_EMAIL and BOOTSTRAP_ADMIN_PASSWORD.
docker compose up --build -d
Invoke-RestMethod http://localhost:5080/health/live
Invoke-RestMethod http://localhost:5080/health/ready
```

Catalogue routes require a token, so register and log in before calling `/api/v1/stores`
— see [API v1](#api-v1) for a complete PowerShell walkthrough.

The API is at `http://localhost:5080`; development OpenAPI JSON is at
`http://localhost:5080/openapi/v1.json`. Compose waits for PostgreSQL, applies migrations,
and seeds two stores, dishes, drinks, and roles. It runs the API as a non-root user.
Only loopback ports are published. `.env` is ignored and deliberately has no usable
defaults; choose a local database password without connection-string delimiters (`;`).
Never reuse production credentials.

The optional bootstrap administrator must use a **new** email and a password of at
least 12 characters with uppercase, lowercase, a digit, and a non-alphanumeric character.
Bootstrap never resets an existing account's password or promotes an existing customer.
Remove bootstrap settings after provisioning; they are not needed for subsequent starts.
Public registration only creates customers, never staff or administrators.

```powershell
docker compose logs -f api
docker compose down
# Destructive local reset, including database AND token-protection keys:
docker compose down -v
```

## Run and debug in VS Code on Windows

Install the .NET 10 SDK, Docker Desktop, and the recommended C# Dev Kit extension.
Start only the database and configure the API's development user-secrets:

```powershell
docker compose up -d db
dotnet tool restore
dotnet restore BashCantina.slnx
$dbPassword = [System.Net.NetworkCredential]::new('', (Read-Host 'Database password from .env' -AsSecureString)).Password
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=cantina;Username=cantina;******" --project src/Cantina.Api
dotnet run --project src/Cantina.Api --launch-profile http
```

Alternatively press **F5**, selecting **Bash Cantina API**. The VS Code launch configuration
uses port 5080 and applies local migrations. User-secrets are outside the repository but
are not encrypted; use them only for development. `.env` is read by Compose, **not** by
`dotnet run`. Do not run both the Compose API and the VS Code API on the same host port.
For local staff provisioning, set `BootstrapAdmin:Email` and `BootstrapAdmin:Password`
using user-secrets before starting with migrations enabled.

## API v1

All business endpoints are under `/api/v1`; an unsupported version is a 404, never an
implicit fallback. Every response carries `api-supported-versions`. JSON uses camelCase.
Item `kind` is numeric: **1 = Dish, 2 = Drink**. Prices have two decimal places; image
URLs must be absolute HTTPS URLs (stored, not fetched). Stores are addressed by slug.

| Method | Path | Access |
|---|---|---|
| POST | `/api/v1/auth/register` | Public, rate-limited |
| POST | `/api/v1/auth/login` | Public, rate-limited |
| POST | `/api/v1/auth/refresh` | Valid refresh token, rate-limited |
| POST | `/api/v1/auth/logout` | Authenticated |
| GET | `/api/v1/auth/me` | Authenticated |
| GET | `/api/v1/auth/external/providers` | Public, rate-limited |
| GET | `/api/v1/auth/external/{provider}/start` | Public, rate-limited |
| GET | `/api/v1/auth/external/callback` | Provider redirect |
| POST | `/api/v1/auth/external/exchange` | One-time login code |
| GET | `/api/v1/stores` | Authenticated |
| GET | `/api/v1/stores/{storeSlug}/menu-items` | Authenticated |
| GET | `/api/v1/stores/{storeSlug}/menu-items/{itemId}` | Authenticated |
| GET | `/api/v1/stores/{storeSlug}/menu-items/{itemId}/ratings` | Authenticated |
| POST | `/api/v1/stores/{storeSlug}/menu-items` | Admin or store's StoreManager |
| PUT | `/api/v1/stores/{storeSlug}/menu-items/{itemId}` | Admin or store's StoreManager |
| DELETE | `/api/v1/stores/{storeSlug}/menu-items/{itemId}` | Admin or store's StoreManager |
| PUT | `/api/v1/stores/{storeSlug}/menu-items/{itemId}/rating` | Customer |
| GET | `/api/v1/stores/{storeSlug}/reviews` | Admin or store's StoreManager |
| GET | `/api/v1/stores/{storeSlug}/reviews/summary` | Admin or store's StoreManager |

Registration and login are the only anonymous business routes; without them no session
could ever be obtained. Health probes are an operational exception, not an API route.

Seeded store slugs: `bash` and `sportscene`. An unknown or inactive slug returns `404`.

Catalogue query parameters: `search` (literal, case-insensitive name/description search,
up to 120 characters), `kind`, `minPrice`, `maxPrice`, `sort` (`name`, `-name`, `price`,
`-price`, `rating`), `page` (default 1), `pageSize` (default 20, maximum 100).
Ordering always includes an ID tie-breaker. Responses contain `items`, `pageNumber`,
`pageSize`, and `totalCount`. Items include `averageRating` (null when unrated) and
`ratingCount`. A customer has one rating per item; submitting again updates it.

The ratings list is newest-first and takes `page`/`pageSize`. It returns `score`,
`reviewText`, `createdAtUtc` and `updatedAtUtc`. Reviewer identity is deliberately not
exposed. `totalCount` costs one `COUNT` per request; it is served from the catalogue
cache for menu pages but not for ratings.

Menu create/update body:

```json
{
  "kind": 1,
  "name": "Bash burger",
  "description": "Grilled burger with chips",
  "price": 89.99,
  "imageUrl": "https://example.com/images/burger.jpg"
}
```

Rating body — `score` is required and between 1 and 5, `reviewText` is optional, trimmed,
and at most 1000 characters:

```json
{ "score": 5, "reviewText": "Worth the queue." }
```

Registration body — `name` is required (1–120 characters, trimmed), `email` must be unique,
and `password` must satisfy the Identity policy:

```json
{ "name": "Chalmun", "email": "chalmun@example.com", "password": "Correct!Horse123" }
```

`GET /api/v1/auth/me` returns `id`, `name`, `email`, and `roles`.

Authentication and a rating, using PowerShell:

```powershell
$base = 'http://localhost:5080/api/v1'
$name = Read-Host 'Customer name'
$email = Read-Host 'Customer email'
$password = [System.Net.NetworkCredential]::new('', (Read-Host 'Customer password' -AsSecureString)).Password
$registration = @{ name = $name; email = $email; password = $password } | ConvertTo-Json
$credentials = @{ email = $email; password = $password } | ConvertTo-Json
Invoke-RestMethod "$base/auth/register" -Method Post -ContentType application/json -Body $registration
$tokens = Invoke-RestMethod "$base/auth/login" -Method Post -ContentType application/json -Body $credentials
$headers = @{ Authorization = "******" }
$page = Invoke-RestMethod "$base/stores/bash/menu-items?kind=1&sort=price&pageSize=10" -Headers $headers
$item = $page.items[0].id
Invoke-RestMethod "$base/stores/bash/menu-items/$item/rating" -Method Put -Headers $headers -ContentType application/json -Body '{"score":5,"reviewText":"Worth the queue."}'
# The same token reads the second storefront without logging in again.
Invoke-RestMethod "$base/stores/sportscene/menu-items" -Headers $headers
$refresh = @{ refreshToken = $tokens.refreshToken } | ConvertTo-Json
Invoke-RestMethod "$base/auth/refresh" -Method Post -ContentType application/json -Body $refresh
Invoke-RestMethod "$base/auth/logout" -Method Post -Headers $headers
```

Registration returns a generic `202 Accepted` for eligible and duplicate accounts to
avoid revealing account existence. Malformed input returns `400`. Login failures and
locked accounts return the same `401`. Successful login/refresh returns `tokenType`,
`accessToken`, `expiresIn`, and `refreshToken`. Tokens are **opaque ASP.NET Core Identity
bearer tokens, not JWTs**, and cannot be decoded by a client. No authentication cookies
are issued. Store roles are not accepted from request bodies. `POST /auth/logout` rotates
the account's security stamp, which revokes every access and refresh token already issued
to it; a later call with the old token returns `401`.

Staff provisioning is an operator task, not a public API: create the user with Identity,
assign `StoreManager` through `UserManager`, and add a `StoreMembership` for the store.
`Admin` is intentionally backend-wide. Store managers need both the role and a current
membership; knowing a different store's item ID never grants access. Customers use the
same account/token across stores but cannot write menu data. Memberships and roles must
only be managed through trusted administrative tooling.

Errors use RFC 7807 `application/problem+json`, including validation `errors` and
`correlationId`. `X-Correlation-ID` is returned on responses; a supplied ID is accepted
only if it is at most 64 safe ASCII characters. Keep this value when reporting problems.
`401`, `403`, `404`, `405`, `409`, `422`, and `429` also use ProblemDetails; `429` includes
`Retry-After`. A `409` means the menu item changed between your read and your write:
re-read it and retry.

## Review dashboard

The Sith-apprentice scenario is a moderation problem, not a browsing one, so the staff view is
separate from the public per-item ratings list and does disclose the reviewer.

```http
GET /api/v1/stores/bash/reviews?maxScore=2&withTextOnly=true&sort=-created&pageSize=50
GET /api/v1/stores/bash/reviews/summary?fromUtc=2026-10-01T00:00:00Z
```

Filters: `minScore`, `maxScore`, `menuItemId`, `search` (review text), `fromUtc`, `toUtc`,
`withTextOnly`, `sort` (`updated`, `-updated`, `created`, `-created`, `score`, `-score`),
`page`, `pageSize`. The list returns the menu item, the reviewer's id/name/email, the score,
the review text and both timestamps.

`/summary` applies the same filters and returns `totalReviews`, `distinctReviewers`,
`averageScore`, the full 1–5 `distribution`, the ten `mostActiveReviewers`
(count, average, first and last review) and the ten `lowestRatedItems`. A coordinated
review campaign shows up as a few accounts holding a large share of the reviews inside a
narrow window. Aggregates are computed in PostgreSQL and are never served from the
catalogue cache, so moderation never sees stale data.

Access is `Admin` or the store's own `StoreManager`; a manager of one store cannot read
another store's reviews, and customers get `403`.

## Signing in with an external provider (OAuth 2.0 / OIDC)

Any OpenID Connect provider can be enabled by configuration — no code changes:

```text
Authentication__External__ReturnUrls__0=http://localhost:3000/signin
Authentication__External__Providers__google__DisplayName=Google
Authentication__External__Providers__google__Authority=https://accounts.google.com
Authentication__External__Providers__google__ClientId=<client id>
Authentication__External__Providers__google__ClientSecret=<client secret>
```

Register `http://localhost:5080/signin-google` as the provider's redirect URI. When no
provider is configured the handlers are never registered and the start endpoint returns `404`.

1. `GET /api/v1/auth/external/providers` lists what this deployment enabled.
2. `GET /api/v1/auth/external/google/start?returnUrl=...` redirects to the provider
   (Authorization Code + PKCE). `returnUrl` must match the allow list exactly.
3. The provider returns to `/api/v1/auth/external/callback`, which resolves the account and
   redirects to `returnUrl?code=...`.
4. `POST /api/v1/auth/external/exchange` with `{ "code": "..." }` returns the same
   `accessToken`/`refreshToken` pair as password login.

The code is single-use and lives for two minutes. **Tokens are never placed in a URL.**
An account is linked to a provider identity only when the provider asserts a *verified*
email; otherwise the callback returns `403`. Externally provisioned users are ordinary
customers and are subject to the same lockout, logout and revocation rules.

Codes are bound to the account's security stamp, so logout or administrative revocation also
invalidates outstanding codes. Accounts with local two-factor authentication enabled cannot
exchange codes because this API has no second-factor flow. Consumption is atomic and retained
without eviction until expiry; a full consumed-code ledger rejects exchanges rather than
allowing replay. Like the catalogue cache, this ledger assumes a single running API instance;
codes fail closed across restarts or attempts to exchange on a different instance.

## Retrying a request safely

`POST /api/v1/stores/{storeSlug}/menu-items` accepts an optional `Idempotency-Key` header
(up to 128 characters of `A-Z a-z 0-9 - _ . :`). If a request times out, retry it with the
same key:

| Situation | Result |
|---|---|
| Key unseen | The item is created and the response recorded |
| Same key and body, finished | Original response replayed, `Idempotency-Replayed: true` |
| Same key and body, still running | `409` with `Retry-After` |
| Same key, different body | `422` |
| Request failed before commit | The item is rolled back and the key released |
| Response delivery failed after commit | Original response replayed; the item is not created again |

The item and completed replay record commit in one transaction before response delivery.
Keys are scoped per user and expire after 24 hours, including on lookup. Abandoned reservations
can be reclaimed after two minutes; ownership checks and row locking prevent an old caller
from writing through or releasing a replacement reservation. `PUT` and `DELETE` are already
idempotent and need no key. See [ADR 0010](docs/adr/0010-failure-recovery.md) for how
rollback, transient retry and replay fit together.

## Architecture and data

* **Cantina.Domain**: stores, menu items, ratings, memberships; no framework dependencies.
* **Cantina.Application**: feature contracts, validation, menu commands/queries, and the
  `IMenuRepository`, `IReviewDashboard` and `IIdempotencyStore` ports. No EF Core or HTTP
  dependencies.
* **Cantina.Infrastructure**: EF Core 10/Npgsql, Identity stores, migrations, seed data,
  async repository implementations, transactional account provisioning, and a bounded
  catalogue cache.
* **Cantina.Api**: HTTP composition, versioned feature endpoints, authentication and
  external OIDC sign-in, store-aware authorization, rate limiting, idempotent replay,
  error handling, logging, and health probes.

Store slug resolution lives in one place (`MapStoreEndpoints`) and publishes a request-scoped
`StoreContext`, so every feature mounted under `/stores/{storeSlug}` — catalogue and reviews
today, anything else tomorrow — inherits resolution and `404` handling without repeating it.
Read and write concerns are segregated into separate ports rather than one growing repository
interface.

Reads use no-tracking DTO projections, database filtering/sorting, and bounded pagination,
not full-table materialization. Foreign keys, unique keys, check constraints, and store
indexes enforce integrity. Ratings use a parameterized atomic PostgreSQL upsert with a
composite store/item foreign key. Menu-item updates carry PostgreSQL's `xmin` row version
as a concurrency token, so a lost update returns `409` instead of silently overwriting.
Account creation is transactional, and transient database faults are retried in place.
Shared Identity users have no home-store restriction. PostgreSQL is the only supported
provider, in development, test and production alike.

Design decisions and their trade-offs are recorded as ADRs in [docs/adr](docs/adr):
modular monolith, bearer tokens over cookies, slug routing, the process-local cache,
PostgreSQL-only testing, URL-segment versioning, optimistic concurrency, the review
dashboard, external OIDC login, and failure recovery.

The cache holds at most 1,024 entries for 30 seconds; each catalogue page is capped at
100 items. All mutations invalidate the store generation, including ratings. Generation
checks prevent an overlapping old query from repopulating a stale generation. Metadata
is bounded using fixed stripes; rare stripe collisions merely cause extra cache misses.
The cache is process-local: this initial deployment assumes one API instance. Multiple
instances require shared invalidation (or disabling/replacing the cache); a TTL alone
does not provide immediate cross-instance consistency. Concurrent writes are last-write-wins;
offset pagination does not promise a snapshot across separate requests.

### Migrations and production deployment

```powershell
dotnet tool restore
dotnet ef migrations list --project src/Cantina.Infrastructure --startup-project src/Cantina.Api
# With ConnectionStrings__DefaultConnection set, apply migration + role/bootstrap seeding once:
dotnet run --project src/Cantina.Api -- --initialize-database
# For a published deployment, run the same one-shot mode before starting the API:
dotnet Cantina.Api.dll --initialize-database
# To add a future model change:
dotnet ef migrations add ChangeName --project src/Cantina.Infrastructure --startup-project src/Cantina.Api
```

The checked-in initial migration contains catalogue seed data. The one-shot initializer
also creates Identity roles and optionally a bootstrap administrator. Run it as a
single deployment job using a migration-capable database identity. Normal production
instances should set `Database__ApplyMigrations=false` and use a least-privilege database
identity. Back up the database before schema changes.

Compose is a **local development** configuration, not an internet-facing deployment.
Configuration uses standard ASP.NET Core providers; environment variables use `__`
in place of `:`:

| Environment variable | Purpose / default |
|---|---|
| `ConnectionStrings__DefaultConnection` | Required PostgreSQL connection string; no embedded default |
| `Database__ApplyMigrations` | `false`; local Compose/debug profiles opt in |
| `DataProtection__KeysPath` | Durable token-protection key directory; configure for deployment |
| `BootstrapAdmin__Email`, `BootstrapAdmin__Password` | Optional, new-account-only initial provisioning |
| `Authentication__RateLimit__PermitLimit` | Auth requests per minute per IP, default 10 |
| `Catalogue__RateLimit__PermitLimit` | Catalogue requests per minute per account, default 100 |
| `Authentication__External__ReturnUrls__0` | Allow-listed URL a federated login may finish at |
| `Authentication__External__Providers__<name>__*` | `Authority`, `ClientId`, `ClientSecret`, `DisplayName` |
| `Security__HttpsRedirection` | `true` outside Development |

For production:

* Set `ASPNETCORE_ENVIRONMENT=Production`, a restricted `AllowedHosts`, and an external
  secret source for `ConnectionStrings__DefaultConnection`; require TLS to PostgreSQL.
* Terminate HTTPS securely, configure the HTTPS port/redirection for the deployment,
  and only disable `Security__HttpsRedirection` when a trusted ingress enforces TLS.
  Forwarded headers are not trusted by default. Configure known proxies explicitly
  before using client IP headers; do not allow arbitrary `X-Forwarded-For`.
* Persist `DataProtection__KeysPath` on access-controlled durable storage and encrypt
  keys at rest using the deployment's key-management system. The application name
  remains stable. Losing keys invalidates tokens; sharing an unprotected key directory
  across trust boundaries is unsafe.
* Password hashing and five-failure/15-minute account lockout are provided by Identity,
  which is also the single source of truth for the password policy enforced at registration.
  Auth endpoints allow 10 requests/minute/IP. Catalogue routes allow 100 requests/minute
  and are rate-limited **after** authentication, so the allowance is charged to the
  signed-in account rather than a shared NAT address; anonymous callers fall back to an
  IP partition. A process-wide concurrent-request cap applies to everything except health
  probes, which are deliberately exempt so an overloaded instance still reports accurately.
  Limits are per instance; apply distributed abuse protection at ingress when scaling.
* Access tokens last 15 minutes; refresh tokens last seven days. Account security
  stamps and lockout are checked on authenticated requests and refresh. Refresh tokens
  are reusable until expiry/revocation, not a one-time rotating-token family. Revoke
  sessions through Identity security-stamp updates; role changes should also update
  that stamp. Public password reset/email delivery, email verification, MFA, and a
  session-management UI are not implemented for this assessment.
* Local identity is not an OpenID Connect provider; it is an OIDC *client*. External logins
  are linked through Identity external logins, preserving the local user ID, ratings and
  store memberships, and are only linked on a provider-asserted verified email. Client
  secrets belong in the deployment's secret store, never in the repository. Single-use
  enforcement for login codes is process-local; a multi-replica deployment needs a shared
  store or sticky routing.
* Logs include structured route, duration, status, and correlation fields, not bodies,
  tokens, or passwords. Configure a central sink/retention policy outside this sample.

Liveness is `/health/live`; readiness is `/health/ready`, which runs a registered
health check that asynchronously probes PostgreSQL without disclosing connection details.
Both return `{"status":"healthy"}` or a `503` ProblemDetails. OpenAPI is available only in
Development. The API deliberately does not enable broad CORS or expose public
staff-management routes.

## Tests and quality checks

```powershell
dotnet build BashCantina.slnx -c Release -warnaserror
dotnet test tests/Cantina.UnitTests -c Release
dotnet test tests/Cantina.IntegrationTests -c Release
dotnet test BashCantina.slnx -c Release
dotnet ef migrations has-pending-model-changes --project src/Cantina.Infrastructure --startup-project src/Cantina.Api
dotnet list BashCantina.slnx package --vulnerable --include-transitive
```

Integration tests require a running Docker engine. They use WebApplicationFactory and
Testcontainers with real, isolated PostgreSQL containers, migrations, and actual Identity
authentication, not an in-memory database or authentication bypass. They cover catalogue
CRUD/filtering/paging, validation, roles/store boundaries, slug resolution, ratings with
review text, the ratings listing, cache invalidation, registration/login/refresh/logout,
lockout, token revocation, version reporting, the published OpenAPI contract, health
probes, and both IP- and account-partitioned rate limits. Unit tests cover validation
boundaries. No Docker-dependent tests are silently skipped.

GitHub Actions restores tools/packages, builds with warnings as errors, runs the tests,
checks migration drift and vulnerable dependencies, and builds the container. Performance
benchmarks are kept separate from pass/fail CI to avoid noisy shared-runner thresholds.

## Performance benchmarks

`benchmarks/Cantina.Benchmarks` contains a reproducible BenchmarkDotNet comparison of
tracked full-catalogue materialization, the actual paginated projection, and a warm
catalogue cache against the same PostgreSQL dataset on the same machine. See its
console usage and safety checks before pointing it at a disposable benchmark database.
It refuses any database name other than `cantina_benchmarks`; setup creates a unique
schema and cleanup drops **only that schema**. Use a disposable database anyway:

```powershell
$benchPassword = [Guid]::NewGuid().ToString('N')
docker run --rm -d --name cantina-benchmarks -p 127.0.0.1:55432:5432 -e POSTGRES_DB=cantina_benchmarks -e "POSTGRES_PASSWORD=$benchPassword" postgres:17-alpine
# Wait until this succeeds:
docker exec cantina-benchmarks pg_isready -U postgres -d cantina_benchmarks
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=55432;Database=cantina_benchmarks;Username=postgres;******"
dotnet run -c Release --project benchmarks/Cantina.Benchmarks -- --filter '*CatalogueBenchmarks*' --job short
docker stop cantina-benchmarks
Remove-Item Env:\ConnectionStrings__DefaultConnection
```

The benchmark seeds 10,000 menu items with ratings, checks that all three paths return
the identical complete page, and records timing and allocations. The baseline is an
explicit naive implementation for comparison, not a claim about a previously deployed
application. Warm-cache results measure the repository, not HTTP/authentication overhead.

Measured on 2026-10-28, on the **same host and dataset**: Ubuntu 24.04.5, AMD EPYC
9V74 (4 logical / 2 physical cores), 15 GiB RAM, SDK 10.0.401, .NET 10.0.12,
PostgreSQL 17.11 in Docker. BenchmarkDotNet ShortRun, Release, password-authenticated
database; 10,000 items, 9,999 ratings, 3,100 matches, identical 20-item result pages:

| Implementation | Mean per request | Allocated per request |
|---|---:|---:|
| Tracked full materialization (baseline) | 168.318 ms | 46,679.50 KB |
| Actual repository, uncached SQL projection | 9.343 ms | 109.27 KB |
| Actual repository, warm bounded cache | 3.010 µs | 2.39 KB |

The uncached projection was approximately **18× faster** with **427× less managed
allocation** than the explicit baseline in this run. Every operation creates a fresh
DbContext; the uncached path forces invalidation, and the warm path is prewarmed outside
the timed iteration. Setup and cleanup operate on isolated migrated schemas. ShortRun
uncertainty and shared-host variability are substantial: rerun on your deployment
hardware rather than treating these numbers as a latency SLA. BenchmarkDotNet writes
local reports under ignored `BenchmarkDotNet.Artifacts/results/`.
