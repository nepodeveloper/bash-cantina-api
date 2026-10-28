# Bash Cantina API

A .NET 10 / ASP.NET Core REST API for the TFG Senior Software Engineer assessment.
One modular monolith serves Bash and Sportscene, with a shared customer identity and
store-scoped dishes, drinks, staff permissions, and ratings. No external identity provider
is needed. The optional dashboard is intentionally out of scope.

## Run with Docker (Windows, macOS, or Linux)

Install Docker Desktop (Linux containers), then:

```powershell
Copy-Item .env.example .env
# Edit .env: supply POSTGRES_PASSWORD.
# Optionally supply BOTH BOOTSTRAP_ADMIN_EMAIL and BOOTSTRAP_ADMIN_PASSWORD.
docker compose up --build -d
Invoke-RestMethod http://localhost:5080/health/ready
Invoke-RestMethod http://localhost:5080/api/v1/stores
```

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
implicit fallback. JSON uses camelCase. Item `kind` is numeric: **1 = Dish, 2 = Drink**.
Prices have two decimal places; image URLs must be absolute HTTPS URLs (stored, not fetched).

| Method | Path | Access |
|---|---|---|
| POST | `/api/v1/auth/register` | Public, rate-limited |
| POST | `/api/v1/auth/login` | Public, rate-limited |
| POST | `/api/v1/auth/refresh` | Valid refresh token, rate-limited |
| GET | `/api/v1/auth/me` | Authenticated |
| GET | `/api/v1/stores` | Public |
| GET | `/api/v1/stores/{storeId}/menu-items` | Public |
| GET | `/api/v1/stores/{storeId}/menu-items/{itemId}` | Public |
| POST | `/api/v1/stores/{storeId}/menu-items` | Admin or store's StoreManager |
| PUT | `/api/v1/stores/{storeId}/menu-items/{itemId}` | Admin or store's StoreManager |
| DELETE | `/api/v1/stores/{storeId}/menu-items/{itemId}` | Admin or store's StoreManager |
| PUT | `/api/v1/stores/{storeId}/menu-items/{itemId}/rating` | Customer |

Seeded store IDs:

* Bash: `11111111-1111-1111-1111-111111111111`
* Sportscene: `22222222-2222-2222-2222-222222222222`

Catalogue query parameters: `search` (literal, case-insensitive name/description search,
up to 120 characters), `kind`, `minPrice`, `maxPrice`, `sort` (`name`, `-name`, `price`,
`-price`, `rating`), `page` (default 1), `pageSize` (default 20, maximum 100).
Ordering always includes an ID tie-breaker. Responses contain `items`, `pageNumber`,
`pageSize`, and `totalCount`. Items include `averageRating` (null when unrated) and
`ratingCount`. A customer has one rating per item; submitting again updates it.

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

Authentication and a rating, using PowerShell:

```powershell
$base = 'http://localhost:5080/api/v1'
$email = Read-Host 'Customer email'
$password = [System.Net.NetworkCredential]::new('', (Read-Host 'Customer password' -AsSecureString)).Password
$credentials = @{ email = $email; password = $password } | ConvertTo-Json
Invoke-RestMethod "$base/auth/register" -Method Post -ContentType application/json -Body $credentials
$tokens = Invoke-RestMethod "$base/auth/login" -Method Post -ContentType application/json -Body $credentials
$headers = @{ Authorization = "******" }
$store = '11111111-1111-1111-1111-111111111111'
$page = Invoke-RestMethod "$base/stores/$store/menu-items?kind=1&sort=price&pageSize=10"
$item = $page.items[0].id
Invoke-RestMethod "$base/stores/$store/menu-items/$item/rating" -Method Put -Headers $headers -ContentType application/json -Body '{"score":5}'
$refresh = @{ refreshToken = $tokens.refreshToken } | ConvertTo-Json
Invoke-RestMethod "$base/auth/refresh" -Method Post -ContentType application/json -Body $refresh
```

Registration returns a generic `202 Accepted` for eligible and duplicate accounts to
avoid revealing account existence. Malformed input returns `400`. Login failures and
locked accounts return the same `401`. Successful login/refresh returns `tokenType`,
`accessToken`, `expiresIn`, and `refreshToken`. Tokens are **opaque ASP.NET Core Identity
bearer tokens, not JWTs**, and cannot be decoded by a client. No authentication cookies
are issued. Store roles are not accepted from request bodies.

Staff provisioning is an operator task, not a public API: create the user with Identity,
assign `StoreManager` through `UserManager`, and add a `StoreMembership` for the store.
`Admin` is intentionally backend-wide. Store managers need both the role and a current
membership; knowing a different store's item ID never grants access. Customers use the
same account/token across stores but cannot write menu data. Memberships and roles must
only be managed through trusted administrative tooling.

Errors use RFC 7807 `application/problem+json`, including validation `errors` and
`correlationId`. `X-Correlation-ID` is returned on responses; a supplied ID is accepted
only if it is at most 64 safe ASCII characters. Keep this value when reporting problems.
`401`, `403`, `404`, `405`, and `429` also use ProblemDetails; `429` includes `Retry-After`.

## Architecture and data

* **Cantina.Domain**: stores, menu items, ratings, memberships; no framework dependencies.
* **Cantina.Application**: feature contracts, validation, menu commands/queries, and
  `IMenuRepository`. No EF Core or HTTP dependencies.
* **Cantina.Infrastructure**: EF Core 10/Npgsql, Identity stores, migrations, seed data,
  async repository implementation, and a bounded catalogue cache.
* **Cantina.Api**: HTTP composition, versioned feature endpoints, authentication,
  store-aware authorization, rate limiting, error handling, logging, and health probes.

Reads use no-tracking DTO projections, database filtering/sorting, and bounded pagination,
not full-table materialization. Foreign keys, unique keys, check constraints, and store
indexes enforce integrity. Ratings use a parameterized atomic PostgreSQL upsert with a
composite store/item foreign key. Shared Identity users have no home-store restriction.

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
# To add a future model change:
dotnet ef migrations add ChangeName --project src/Cantina.Infrastructure --startup-project src/Cantina.Api
```

The checked-in initial migration contains catalogue seed data. The one-shot initializer
also creates Identity roles and optionally a bootstrap administrator. Run it as a
single deployment job using a migration-capable database identity. Normal production
instances should set `Database__ApplyMigrations=false` and use a least-privilege database
identity. Back up the database before schema changes.

Compose is a **local development** configuration, not an internet-facing deployment.
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
* Password hashing and five-failure/15-minute account lockout are provided by Identity.
  Auth endpoints allow 10 requests/minute/IP, catalogue routes 100/minute/IP, with a
  process-wide concurrent-request cap. Limits are per instance; apply distributed
  abuse protection at ingress when scaling. NAT clients share an IP allowance.
* Access tokens last 15 minutes; refresh tokens last seven days. Account security
  stamps and lockout are checked on authenticated requests and refresh. Refresh tokens
  are reusable until expiry/revocation, not a one-time rotating-token family. Revoke
  sessions through Identity security-stamp updates; role changes should also update
  that stamp. Public password reset/email delivery, email verification, MFA, and a
  session-management UI are not implemented for this assessment.
* Local identity is not an OpenID Connect provider. Future federation should add a
  trusted OIDC handler and link validated issuer/subject identities through Identity
  external logins, preserving the local user ID, ratings, and store memberships. Do
  not link external identities by unverified email or treat a storefront ID as a user.
* Logs include structured route, duration, status, and correlation fields, not bodies,
  tokens, or passwords. Configure a central sink/retention policy outside this sample.

Liveness is `/health/live`; readiness is `/health/ready` and asynchronously probes
PostgreSQL without disclosing connection details. OpenAPI is available only in Development.
The API deliberately does not enable broad CORS or expose public staff-management routes.

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
CRUD/filtering/paging, validation, roles/store boundaries, ratings/cache invalidation,
registration/login/refresh, lockout, and rate limiting. Unit tests cover validation boundaries.
No Docker-dependent tests are silently skipped.

GitHub Actions restores tools/packages, builds with warnings as errors, runs the tests,
checks migration drift and vulnerable dependencies, and builds the container. Performance
benchmarks are kept separate from pass/fail CI to avoid noisy shared-runner thresholds.

## Performance benchmarks

`benchmarks/Cantina.Benchmarks` contains a reproducible BenchmarkDotNet comparison of
tracked full-catalogue materialization, the actual paginated projection, and a warm
catalogue cache against the same PostgreSQL dataset on the same machine. See its
console usage and safety checks before pointing it at a disposable benchmark database.
Measured results and the exact reproduction command are recorded below after validation.
