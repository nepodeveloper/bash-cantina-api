# End-to-End Testing in Swagger

This walkthrough uses the local SQLite database. It does not require Docker or PostgreSQL.
Run the API from the repository root:

```powershell
dotnet run --project src/Cantina.Api --launch-profile http
```

Open Swagger UI at [http://localhost:5080/swagger](http://localhost:5080/swagger). The OpenAPI document is at [http://localhost:5080/openapi/v1.json](http://localhost:5080/openapi/v1.json). The first run creates `src/Cantina.Api/cantina.db` and seeds the `bash` and `sportscene` stores, menu items, and roles.

Use **Try it out** and **Execute** for each request. Responses use camelCase. Keep the token values private; they are opaque bearer tokens, not readable JWTs.

## 1. Register and Authenticate a Customer

Run `POST /api/v1/auth/register`:

```json
{
  "name": "Swagger Customer",
  "email": "swagger-customer@example.com",
  "password": "ReplaceWithAUnique!123"
}
```

Expected: `202 Accepted`. Use a valid, unique email and a password with at least 12 characters, uppercase, lowercase, a digit, and a non-alphanumeric character.

Run `POST /api/v1/auth/login` with the same email and password. Expected: `200 OK`, with `accessToken`, `refreshToken`, `tokenType`, and `expiresIn` in the response. Copy `accessToken`, click **Authorize**, and paste the token into the Bearer field. Swagger adds the `Bearer` prefix.

Run `GET /api/v1/auth/me`. Expected: `200 OK` with the customer identity and a `Customer` role.

Negative checks:

| Request | Expected result |
|---|---|
| Register with malformed JSON, invalid email, short name, or weak password | `400 Bad Request` with validation details |
| Register the same email again | `202 Accepted`; duplicate and eligible registrations deliberately look alike |
| Login with a wrong password | `401 Unauthorized` |
| Call an authenticated route before authorizing | `401 Unauthorized` |

If Swagger returns `401` after authorization, the token was rejected before the endpoint handler ran. In Swagger's **Authorize** dialog, click **Logout** to clear the cached token, then log in again. Copy the new `accessToken` (not `refreshToken`), reopen **Authorize**, and paste only that token value; the UI adds `Bearer`. Retry the request. Access tokens expire after 15 minutes, and tokens can stop validating if the API's data-protection keys change. If logout itself returns `401`, first refresh or log in, authorize with the newly issued access token, and call logout again. A successful logout returns `204` and invalidates that account's previously issued tokens. If a token was exposed in a screenshot or log, revoke it this way; the rejected token in the screenshot is already unusable for API calls.

## 2. Browse Stores and the Menu

With the customer token authorized, run `GET /api/v1/stores`. Expected: `200 OK`, including `bash` and `sportscene`.

Run `GET /api/v1/stores/bash/menu-items` with these query parameters:

```text
search=burger
kind=1
sort=price
page=1
pageSize=10
```

Expected: `200 OK` with a paged result (`items`, `pageNumber`, `pageSize`, `totalCount`). `kind=1` is Dish and `kind=2` is Drink. Search matches menu names/descriptions without case sensitivity. Copy one returned item's `id` for the next section.

Useful edge checks:

| Request | Expected result |
|---|---|
| `GET /api/v1/stores/not-a-store/menu-items` | `404 Not Found` |
| `GET /api/v1/stores/bash/menu-items?sort=unknown` | `400 Bad Request` |
| `GET /api/v1/stores/bash/menu-items?pageSize=101` | `400 Bad Request` |
| `GET /api/v1/stores/bash/menu-items?minPrice=20&maxPrice=10` | `400 Bad Request` |
| `GET /api/v1/stores/bash/menu-items/{unknown-guid}` | `404 Not Found` |

Supported catalogue sorts are `name`, `-name`, `price`, `-price`, and `rating`. Page size must be from 1 to 100. Search is limited to 120 characters.

## 3. Rate an Item and Read Ratings

Run `PUT /api/v1/stores/bash/menu-items/{itemId}/rating` using an item ID from the previous response:

```json
{
  "score": 5,
  "reviewText": "Great first test"
}
```

Expected: `204 No Content`. Run it again for the same item with a different score or review text. Expected: `204`; each customer has one rating per item, so the second request updates the first rather than adding another rating.

Run `GET /api/v1/stores/bash/menu-items/{itemId}/ratings`. Expected: `200 OK`, a paged list with score and timestamps. Reviewer identity is intentionally not returned by this public endpoint.

Edge checks:

| Request | Expected result |
|---|---|
| Rate with `score` 0 or 6 | `400 Bad Request` |
| Rate with review text longer than 1000 characters | `400 Bad Request` |
| Rate an unknown item ID | `404 Not Found` |
| Read ratings for an unknown item ID | `404 Not Found` |
| Read ratings with `pageSize=101` | `400 Bad Request` |

## 4. Check Customer Permissions

While still authorized as the customer, try `POST /api/v1/stores/bash/menu-items` with a valid menu item body (see the next section). Expected: `403 Forbidden`. A customer can browse and rate, but cannot create, update, delete, or view the staff review dashboard.

## 5. Enable a Local Admin for Staff Scenarios

Public registration never grants staff or administrator roles. To test staff endpoints, configure a bootstrap administrator using .NET user-secrets before starting the API. Choose a new email and a unique password that meets the policy:

```powershell
dotnet user-secrets set "BootstrapAdmin:Email" "swagger-admin@example.com" --project src/Cantina.Api
dotnet user-secrets set "BootstrapAdmin:Password" "UseAUniqueStrong!Password123" --project src/Cantina.Api
```

Restart the API so startup creates the account and assigns `Admin`. Then run `POST /api/v1/auth/login` with those credentials, copy its `accessToken`, and replace the token in Swagger's **Authorize** dialog. The SQLite database retains the account; bootstrap configuration does not reset an existing account's password.

There is deliberately no public staff-management endpoint. Store-manager membership must be provisioned through trusted administrative tooling; the Admin role can exercise the management scenarios below across stores.

## 6. Exercise Menu CRUD as Admin

Run `POST /api/v1/stores/bash/menu-items`:

```json
{
  "kind": 1,
  "name": "Swagger Test Bowl",
  "description": "A menu item created during the Swagger walkthrough.",
  "price": 42.50,
  "imageUrl": "https://example.com/images/swagger-test-bowl.jpg"
}
```

Expected: `201 Created`, a response containing the new item `id`, and a `Location` header. Save the ID.

Then try:

| Action | Expected result |
|---|---|
| `GET /api/v1/stores/bash/menu-items/{id}` | `200 OK` with the created item |
| `PUT /api/v1/stores/bash/menu-items/{id}` with a changed valid body | `200 OK` with updated values |
| `PUT /api/v1/stores/bash/menu-items/{unknown-guid}` | `404 Not Found` |
| `DELETE /api/v1/stores/bash/menu-items/{id}` | `204 No Content` |
| Delete the same ID again | `404 Not Found` |

Validation checks for create/update: use a blank name, kind `3`, negative price, price `1.239`, or an `http://` image URL. Each should return `400 Bad Request`. Valid price range is 0 through 99,999,999.99 with at most two decimal places; image URLs must be absolute HTTPS URLs.

Menu creation accepts an optional `Idempotency-Key` header. The current OpenAPI document does not declare this header, so Swagger UI may not offer an input for it. To test replay behavior, use a client that can set custom headers: repeat the same POST with the same key and body to replay the original `201`; reuse that key with a changed body to get `422`; omit the header to test an ordinary create.

## 7. Review Dashboard as Admin

After the customer has submitted a rating, run `GET /api/v1/stores/bash/reviews` and `GET /api/v1/stores/bash/reviews/summary` as Admin. The list is staff-only and includes reviewer identity; the summary includes counts, score distribution, most active reviewers, and lowest-rated items.

Try filters such as `maxScore=2`, `withTextOnly=true`, `sort=-created`, and `pageSize=50`. Edge checks:

| Request | Expected result |
|---|---|
| Customer token on either review endpoint | `403 Forbidden` |
| `GET /api/v1/stores/bash/reviews?minScore=5&maxScore=1` | `400 Bad Request` |
| `GET /api/v1/stores/bash/reviews?sort=unknown` | `400 Bad Request` |
| `GET /api/v1/stores/bash/reviews?fromUtc=2026-10-29T12:00:00Z&toUtc=2026-10-29T11:00:00Z` | `400 Bad Request` |
| `GET /api/v1/stores/not-a-store/reviews` | `404 Not Found` |

## 8. Refresh and Revoke Tokens

Run `POST /api/v1/auth/refresh` with the `refreshToken` from login:

```json
{
  "refreshToken": "paste-refresh-token-here"
}
```

Expected: `200 OK` with a new access/refresh token pair. Use the new access token in Swagger's **Authorize** dialog.

Run `POST /api/v1/auth/logout` while authorized. Expected: `204 No Content`. Then retry `GET /api/v1/auth/me` with the old token; expected: `401 Unauthorized`. Logout revokes all previously issued tokens for that account.

## Operational Edge Cases

- `429 Too Many Requests` includes `Retry-After`. Authentication is limited to 10 requests per minute per IP; avoid loops while testing.
- Responses include `X-Correlation-ID`. Keep it when reporting a failure.
- `409 Conflict` on an update means the record changed since it was read; fetch the latest item and retry.
- The bearer access token expires after 15 minutes. Refresh it rather than repeatedly logging in.
- SQLite uses a local file and first-run schema creation. It is intended for one local API instance; the checked-in migrations and Docker integration suite remain PostgreSQL-based.