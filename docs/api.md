# API Reference

Authoritative reference for every endpoint the service exposes,
grounded against the actual controllers and DTOs. All routes carry an
implicit `/api/v{version}` prefix (default version `1.0`, supported
range advertised on the `api-supported-versions` response header).

[← back to README](../README.md)

---

## Conventions

### Versioning

- Implemented by `Asp.Versioning.Mvc`. Route template
  `/api/v{version:apiVersion}/[controller]` ([SalesController.cs:22](../src/Ambev.DeveloperEvaluation.WebApi/Features/Sales/SalesController.cs#L22)).
- Default version `1.0` when omitted (`AssumeDefaultVersionWhenUnspecified
  = true`); response always carries
  `api-supported-versions: 1.0`.

### Authentication

- JWT Bearer (`Authorization: Bearer <token>`). Issued by
  `POST /api/v1/auth`.
- **Every endpoint is authenticated by default** via
  `AuthorizationFallbackPolicy = RequireAuthenticatedUser` —
  `[AllowAnonymous]` is opt-in for the four public endpoints.
- The three anonymous endpoints are `POST /api/v1/auth`,
  `POST /api/v1/users`, and the three `/health/*` probes.
- Token signing: HS256, ≥ 32 bytes. `Jwt:Issuer` and `Jwt:Audience` are
  mandatory outside Development.
- Full posture: [security.md](security.md).

### Content negotiation

- Request and success responses: `application/json`.
- Error responses: `application/problem+json` (RFC 7807).

### Standard wrappers

Two envelope types are returned, depending on the endpoint:

```jsonc
// ApiResponseWithData<T> — single resource
{ "success": true, "message": "Sale created successfully", "data": { /* T */ } }

// PaginatedResponse<T> — list endpoint
{
  "success": true,
  "data": [ /* T[] */ ],
  "currentPage": 1,
  "totalPages": 4,
  "totalCount": 37,
  "nextCursor": "eyJzYWxlRGF0ZSI6Ii4uLiJ9"   // only in keyset mode
}
```

Sources:
[`ApiResponseWithData.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Common/ApiResponseWithData.cs),
[`PaginatedResponse.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Common/PaginatedResponse.cs).

### Pagination, ordering, filtering

`_`-prefixed query parameters per the template's
[General API conventions](../spec/general-api.md):

| Parameter | Meaning |
|---|---|
| `_page` | 1-based page number (offset/page mode) |
| `_size` | page size, max enforced by validator |
| `_order` | comma-separated, e.g. `_order=saleDate desc, totalAmount asc` |
| `_cursor` | opaque cursor for keyset pagination (mutually exclusive with `_page`) |
| `_minDate` / `_maxDate` | range filter on `SaleDate` |
| `saleNumber` | substring with `*` wildcard (e.g. `saleNumber=SALE-*`) |
| `customerId`, `branchId` | exact-match filters |
| `isCancelled` | `true` / `false` filter |

### Error format

All errors are RFC 7807 problem details:

```json
{
  "type":     "https://httpstatuses.io/400",
  "title":    "Validation failed",
  "status":   400,
  "detail":   "2 validation errors occurred.",
  "instance": "/api/v1/sales",
  "errors": {
    "Items[0].Quantity": ["Quantity per item must be between 1 and 20."],
    "SaleNumber":        ["'Sale Number' must not be empty."]
  }
}
```

`errors` is populated only on validation problems (400). The exception
→ ProblemDetails translation lives in
[`ValidationExceptionMiddleware.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Middleware/ValidationExceptionMiddleware.cs).

### Status code matrix

| HTTP | Source |
|---|---|
| 200 | success on read / cancel / item-cancel / delete |
| 201 | `POST /api/v1/sales`, `POST /api/v1/users` |
| 400 | `ValidationException`, `DomainException` |
| 401 | missing or invalid bearer token |
| 404 | `ResourceNotFoundException` |
| 409 | `ConflictException`, Postgres 23505 (unique violation), `DbUpdateConcurrencyException`, idempotency in-flight collision |
| 412 | `PreconditionFailedException` (stale `If-Match`) |
| 422 | `Idempotency-Key` reuse with a different body |
| 429 | rate limit exceeded (100/min principal; 5/min IP for `/auth`) |
| 500 | unhandled exception (stack trace logged via Serilog, never echoed) |

### Concurrency & idempotency headers

| Header | Direction | Notes |
|---|---|---|
| `ETag` | response | Strong, hex of `RowVersion`. Returned by `GET/POST/PUT/PATCH` on a sale. |
| `If-Match` | request | Honoured by `PUT` and `DELETE`. Stale → 412. `*` means "don't check". |
| `Idempotency-Key` | request | POST only, ≤ 256 bytes. Replay returns the cached 201 byte-equal to the original; reuse with a different body → 422. Backed by Redis when configured. |
| `Location` | response | Returned by 201 responses, points at the canonical `GET` URL. |

### Rate limiting

- **Default policy (`api`)**: 100 requests / minute / principal
  (`ClaimTypes.NameIdentifier`, falls back to remote IP for anonymous
  callers). Configurable via `RateLimit:PermitLimit` and
  `RateLimit:WindowSeconds`.
- **Strict policy (`auth-strict`)** on `POST /api/v1/auth`: 5 requests
  / minute / IP. Configurable via `RateLimit:AuthPermitLimit` and
  `RateLimit:AuthWindowSeconds`.
- Over-limit responses: 429 Too Many Requests, no body.

---

## Sales

Controller:
[`SalesController.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Sales/SalesController.cs).

### `POST /api/v1/sales` — Create a sale

Creates a sale with all its items. Honours `Idempotency-Key`.

#### Request

```http
POST /api/v1/sales HTTP/1.1
Authorization: Bearer eyJhbGciOi...
Content-Type: application/json
Idempotency-Key: 5a8c0e2e-3e7a-4f3a-91c7-7d5a3e1f9b22

{
  "saleNumber":   "SALE-2026-0001",
  "saleDate":     "2026-05-10T13:45:00Z",
  "customerId":   "8a6f4d4b-1b5c-4d8a-aa0a-6f6f59e5c5c5",
  "customerName": "Acme Ltd.",
  "branchId":     "f1c8a4b3-2a1d-4d11-9b0a-1a1c2e3d4f5a",
  "branchName":   "Downtown",
  "items": [
    { "productId": "11111111-1111-1111-1111-111111111111", "productName": "Beer 350ml", "quantity": 4, "unitPrice": 5.00 },
    { "productId": "22222222-2222-2222-2222-222222222222", "productName": "Bottle Opener", "quantity": 1, "unitPrice": 12.50 }
  ]
}
```

#### Validation (FluentValidation)

Source:
[`CreateSaleValidator.cs`](../src/Ambev.DeveloperEvaluation.Application/Sales/CreateSale/CreateSaleValidator.cs).

| Field | Rule |
|---|---|
| `saleNumber` | required, ≤ 50 chars |
| `saleDate` | > 2000-01-01 UTC, ≤ now + 1 day |
| `customerId` / `branchId` | non-empty GUID |
| `customerName` / `branchName` | required, ≤ 200 chars |
| `items` | non-empty, ≤ 100 elements |
| `items[].productId` | non-empty GUID |
| `items[].productName` | required, ≤ 200 chars |
| `items[].quantity` | between 1 and 20 (inclusive) |
| `items[].unitPrice` | > 0 |
| **Aggregate invariant** | each `productId` may appear **at most once** in the items array |

#### Response — 201 Created

```http
HTTP/1.1 201 Created
Location: /api/v1/sales/3a2b1c0d-...
ETag: "1"
Content-Type: application/json

{
  "success": true,
  "message": "Sale created successfully",
  "data": {
    "id":               "3a2b1c0d-...",
    "saleNumber":       "SALE-2026-0001",
    "saleDate":         "2026-05-10T13:45:00Z",
    "customerId":       "8a6f4d4b-1b5c-4d8a-aa0a-6f6f59e5c5c5",
    "customerName":     "Acme Ltd.",
    "branchId":         "f1c8a4b3-2a1d-4d11-9b0a-1a1c2e3d4f5a",
    "branchName":       "Downtown",
    "totalAmount":      30.50,
    "isCancelled":      false,
    "createdAt":        "2026-05-10T13:45:01.234Z",
    "updatedAt":        null,
    "rowVersion":       1,
    "activeItemsCount": 2,
    "items": [
      { "id": "...", "productId": "11111111-...", "productName": "Beer 350ml",   "quantity": 4, "unitPrice": 5.00,  "discount": 2.00, "totalAmount": 18.00, "isCancelled": false },
      { "id": "...", "productId": "22222222-...", "productName": "Bottle Opener","quantity": 1, "unitPrice": 12.50, "discount": 0.00, "totalAmount": 12.50, "isCancelled": false }
    ]
  }
}
```

#### Error responses

| Status | Body title | Trigger |
|---|---|---|
| 400 | Validation failed | any FluentValidation rule |
| 400 | Domain rule violated | per-product `productId` duplicated; quantity > 20; cancel-on-cancelled |
| 409 | Conflict | duplicate `saleNumber` (cheap pre-check or DB unique violation) |
| 409 | Idempotency-Key in flight | concurrent request with the same key still running |
| 422 | Idempotency-Key reuse with different payload | same key, body whose canonical hash differs |

### `GET /api/v1/sales/{id}` — Get a sale

Returns the full body (header + items). Backed by the 2nd-level read
cache (60 s TTL, evicted on every write to the same id).

```http
GET /api/v1/sales/3a2b1c0d-... HTTP/1.1
Authorization: Bearer ...
```

Response: `200 OK`, `ETag: "1"`, body shape identical to the 201 above
but envelope is `ApiResponseWithData<SaleDto>`.

Errors:

| Status | Trigger |
|---|---|
| 404 | sale not found |

### `GET /api/v1/sales` — List sales

Two modes, mutually exclusive — passing both `_page` and `_cursor`
returns 400.

#### Offset / page mode (default)

```http
GET /api/v1/sales?_page=2&_size=20&_order=saleDate%20desc&customerId=8a6f4d4b-... HTTP/1.1
Authorization: Bearer ...
```

Response:

```json
{
  "success":     true,
  "data":        [ /* SaleSummaryDto[] */ ],
  "currentPage": 2,
  "totalPages":  4,
  "totalCount":  78
}
```

`SaleSummaryDto` is header-only — call `GET /api/v1/sales/{id}` for the
items. Sources:
[`SaleSummaryDto.cs`](../src/Ambev.DeveloperEvaluation.Application/Sales/Common/SaleSummaryDto.cs),
[`ListSalesRequest.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Sales/ListSales/ListSalesRequest.cs).

#### Keyset / cursor mode

```http
GET /api/v1/sales?_size=20&_cursor=eyJzYWxlRGF0ZSI6Ii4uIn0%3D HTTP/1.1
```

Response includes `nextCursor` (string, opaque) when more pages exist;
omitted when the cursor reached the end. `totalPages` and `totalCount`
are omitted (no COUNT(\*) is run). Ordering is fixed to
`SaleDate DESC, Id DESC` for stability.

#### Supported `_order` fields

`SaleNumber`, `SaleDate`, `TotalAmount`, `IsCancelled`, `CreatedAt`,
`UpdatedAt`. Unknown columns return 400. Source:
[`SaleListFilter.cs:15`](../src/Ambev.DeveloperEvaluation.Domain/Repositories/SaleListFilter.cs#L15).

#### Errors

| Status | Trigger |
|---|---|
| 400 | `_page` and `_cursor` both set |
| 400 | `_size` above the configured max |
| 400 | `_order` references an unsupported field |
| 400 | `_minDate > _maxDate` |

### `PUT /api/v1/sales/{id}` — Update a sale

Diff-based update. Existing items matching a request line by
`ProductId` are updated in place (preserving the SaleItem id so
external integrations referencing it stay valid). Items present in the
DB but absent from the request are removed. New product ids are
inserted.

```http
PUT /api/v1/sales/3a2b1c0d-... HTTP/1.1
Authorization: Bearer ...
Content-Type: application/json
If-Match: "1"

{
  "saleDate":     "2026-05-10T13:45:00Z",
  "customerId":   "8a6f4d4b-...",
  "customerName": "Acme Ltd.",
  "branchId":     "f1c8a4b3-...",
  "branchName":   "Downtown",
  "items": [
    { "productId": "11111111-...", "productName": "Beer 350ml", "quantity": 10, "unitPrice": 5.00 }
  ]
}
```

`SaleNumber` is immutable and absent from `UpdateSaleRequest` — the
route id identifies the sale.
[`UpdateSaleRequest.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Sales/UpdateSale/UpdateSaleRequest.cs).

Response: `200 OK`, fresh `ETag`, full body.

#### Errors

| Status | Trigger |
|---|---|
| 400 | validation error or domain rule (duplicate productId, qty > 20, update on cancelled) |
| 404 | unknown id |
| 409 | concurrent update raced this one |
| 412 | `If-Match` doesn't match current row version |

#### <a id="put-known-limitation"></a>Previously known limitation (resolved)

A PUT that introduced new `productId`s used to raise a spurious 409
from EF Core's optimistic concurrency check. The real cause was EF
Core 8's "is this entity new?" heuristic mis-classifying
aggregate-assigned `Guid` keys (we set them via `Guid.NewGuid()` in
the `Sale` / `SaleItem` constructors) — EF emitted
`UPDATE WHERE Id=…` (matching zero rows) instead of `INSERT`. Fixed
with `Property(s => s.Id).ValueGeneratedNever()` on both
`SaleConfiguration` and `SaleItemConfiguration`; the
`UpdateSale_ReplacesAllItems` integration test (no `[Skip]`) now
covers the full-replace path end-to-end. No client workaround
required.

### `DELETE /api/v1/sales/{id}` — Hard delete

```http
DELETE /api/v1/sales/3a2b1c0d-... HTTP/1.1
Authorization: Bearer ...
If-Match: "1"
```

Cascades to all `SaleItems`. Response:

```http
HTTP/1.1 204 No Content
```

204 No Content is the REST-idiomatic success for a delete — no body,
no envelope. Clients distinguish "deleted now" (204) from "already
gone" (404).

| Status | Trigger |
|---|---|
| 404 | unknown id |
| 412 | stale `If-Match` |

### `PATCH /api/v1/sales/{id}/cancel` — Soft cancel

Sets `IsCancelled = true`, cascades to active items, recalculates
total to 0, raises `sale.cancelled.v1`. **Idempotent**: a second
cancel is a no-op and does not emit a second event.

```http
PATCH /api/v1/sales/3a2b1c0d-.../cancel HTTP/1.1
```

Response: `200 OK`, fresh ETag, full body with `isCancelled: true` and
all items flagged `isCancelled: true`.

### `PATCH /api/v1/sales/{id}/items/{itemId}/cancel` — Cancel one item

Cancels a single line and recalculates the sale total. **Idempotent**
on the item.

```http
PATCH /api/v1/sales/3a2b1c0d-.../items/c0ffee-.../cancel HTTP/1.1
```

| Status | Trigger |
|---|---|
| 200 | item cancelled (or already was) |
| 400 | item not part of this sale |
| 404 | sale not found |

---

## Auth

Controller:
[`AuthController.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Auth/AuthController.cs).

### `POST /api/v1/auth` — Authenticate

Anonymous. Rate-limited by the `auth-strict` policy (5 req/min/IP by
default).

```http
POST /api/v1/auth HTTP/1.1
Content-Type: application/json

{ "email": "user@example.com", "password": "S0meP@ss!" }
```

Success:

```json
{
  "success": true,
  "data": {
    "token":    "eyJhbGciOi...",
    "email":    "user@example.com",
    "username": "...",
    "role":     "Customer"
  }
}
```

**Login does not leak user enumeration.** Failure responses use a
single `Invalid credentials` message and run BCrypt verification even
on unknown emails (constant-time path with a frozen dummy hash) so
response timing cannot be used as an oracle. See
[`AuthenticateUserHandler.cs`](../src/Ambev.DeveloperEvaluation.Application/Auth/AuthenticateUser/AuthenticateUserHandler.cs).

| Status | Trigger |
|---|---|
| 400 | malformed body |
| 401 | bad password OR unknown email |
| 429 | rate limit (5/min/IP) |

---

## Users

Controller:
[`UsersController.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Users/UsersController.cs).

### `POST /api/v1/users` — Self-service signup (anonymous)

Public on purpose. The handler **hard-codes `role=Customer` +
`status=Active`** — `CreateUserRequest` does not expose those fields,
defeating mass-assignment / privilege-escalation attempts.
[`CreateUserRequest.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Users/CreateUser/CreateUserRequest.cs).

```http
POST /api/v1/users HTTP/1.1
Content-Type: application/json

{
  "username": "alice",
  "email":    "alice@example.com",
  "password": "S0meP@ss!",
  "phone":    "(11) 99999-9999"
}
```

`201 Created` with `Location: /api/v1/users/{id}`.

| Status | Trigger |
|---|---|
| 400 | validation failure (password policy, phone format, etc.) |
| 409 | email or username already in use |

### `GET /api/v1/users/{id}` — Get user by id (authenticated)

`200 OK` with `GetUserResponse`. `404` if not found.

### `DELETE /api/v1/users/{id}` — Delete user (authenticated)

`200 OK` with `ApiResponse`. `404` if not found.

---

## Health

Anonymous probes. The Postgres readiness probe is the gate for
load-balancer traffic.

| Route | Tags | Purpose |
|---|---|---|
| `/health/live` | `liveness` | Process is up. Always green unless the host has died. |
| `/health/ready` | `readiness` | Process + Postgres (`AddDbContextCheck`). 503 when DB is unreachable. |
| `/health` | all | Full report (catch-all for inspection). |

Outside Development, exception details are **stripped** from the
response payload to avoid leaking connection strings or stack-trace
fragments to anonymous callers. The full detail is in the server log.

Source:
[`HealthChecksExtension.cs`](../src/Ambev.DeveloperEvaluation.Common/HealthChecks/HealthChecksExtension.cs).

---

## Swagger

Behind a feature flag (`Swagger:Enabled`) — never tied directly to
`ASPNETCORE_ENVIRONMENT=Development`, so a misconfigured prod deploy
that inherits the dev env var doesn't expose the full contract. When
enabled: <https://localhost:8081/swagger>.

---

[← back to README](../README.md)
