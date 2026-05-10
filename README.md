# Developer Evaluation Project

`READ CAREFULLY`

## Use Case
**You are a developer on the DeveloperStore team. Now we need to implement the API prototypes.**

As we work with `DDD`, to reference entities from other domains, we use the `External Identities` pattern with denormalization of entity descriptions.

Therefore, you will write an API (complete CRUD) that handles sales records. The API needs to be able to inform:

* Sale number
* Date when the sale was made
* Customer
* Total sale amount
* Branch where the sale was made
* Products
* Quantities
* Unit prices
* Discounts
* Total amount for each item
* Cancelled/Not Cancelled

It's not mandatory, but it would be a differential to build code for publishing events of:
* SaleCreated
* SaleModified
* SaleCancelled
* ItemCancelled

If you write the code, **it's not required** to actually publish to any Message Broker. You can log a message in the application log or however you find most convenient.

### Business Rules

* Purchases above 4 identical items have a 10% discount
* Purchases between 10 and 20 identical items have a 20% discount
* It's not possible to sell above 20 identical items
* Purchases below 4 items cannot have a discount

These business rules define quantity-based discounting tiers and limitations:

1. Discount Tiers:
   - 4+ items: 10% discount
   - 10-20 items: 20% discount

2. Restrictions:
   - Maximum limit: 20 items per product
   - No discounts allowed for quantities below 4 items

## Overview
This section provides a high-level overview of the project and the various skills and competencies it aims to assess for developer candidates. 

See [Overview](/.doc/overview.md)

## Tech Stack
This section lists the key technologies used in the project, including the backend, testing, frontend, and database components. 

See [Tech Stack](/.doc/tech-stack.md)

## Frameworks
This section outlines the frameworks and libraries that are leveraged in the project to enhance development productivity and maintainability. 

See [Frameworks](/.doc/frameworks.md)

<!-- 
## API Structure
This section includes links to the detailed documentation for the different API resources:
- [API General](./docs/general-api.md)
- [Products API](/.doc/products-api.md)
- [Carts API](/.doc/carts-api.md)
- [Users API](/.doc/users-api.md)
- [Auth API](/.doc/auth-api.md)
-->

## Project Structure
This section describes the overall structure and organization of the project files and directories. 

See [Project Structure](/.doc/project-structure.md)

---

## Sales API

The Sales feature is the implementation of the use case described above —
a complete CRUD with quantity-tier discount rules, soft-cancel semantics,
and a transactional outbox dispatching domain events. All endpoints are
public (no `[Authorize]`).

### Endpoints

| Verb | Route | Description |
|---|---|---|
| `POST` | `/api/sales` | Create a sale (header + items). Honours `Idempotency-Key`. |
| `GET` | `/api/sales/{id}` | Get a sale by id (full body with items). |
| `GET` | `/api/sales` | List sales (paginated, filtered, ordered) — header-only summaries. |
| `PUT` | `/api/sales/{id}` | Diff-based update: existing items keep their id when only quantity/price changes. |
| `DELETE` | `/api/sales/{id}` | Hard-delete a sale and its items (cascade). |
| `PATCH` | `/api/sales/{id}/cancel` | Soft-cancel a sale (idempotent). |
| `PATCH` | `/api/sales/{id}/items/{itemId}/cancel` | Cancel a single line and recalculate the total. |

The list endpoint follows the conventions in
[`/.doc/general-api.md`](.doc/general-api.md): `_page`, `_size`, `_order`,
plus `_minDate` / `_maxDate`, `customerId`, `branchId`, `isCancelled`,
`saleNumber` (substring with `*`).

### Error contract

Errors come back as RFC 7807 `application/problem+json` payloads:

| Exception | HTTP | Title |
|---|---|---|
| `ValidationException` (FluentValidation) | 400 | Validation failed |
| `DomainException` | 400 | Domain rule violated |
| `ResourceNotFoundException` | 404 | Resource not found |
| `ConflictException` / unique-violation / `DbUpdateConcurrencyException` | 409 | Conflict / Concurrent modification |
| `UnauthorizedAccessException` | 401 | Unauthorized |
| Any other | 500 | Internal server error (logged with full stack via Serilog) |

### Discount rules

Per product, across non-cancelled lines. Adding the same product twice
with **different unit price or product name** is rejected — caller must
consolidate before sending. Same product with the same price merges
quantity so the 20-cap can't be bypassed by splitting lines.

| Quantity (per product) | Discount |
|---|---|
| 1–3 | 0% |
| 4–9 | 10% |
| 10–20 | 20% |
| above 20 | not allowed (HTTP 400) |

### Domain events & outbox

Each Sales handler stages events into an `OutboxMessages` table inside the
same transaction that persists the aggregate, and a hosted background
service (`OutboxDispatcherService`) polls the table every 5 seconds and
emits each pending message via the application log — so events survive a
crash between SaveChanges and publish. To replace the logger with a real
broker, swap the body of `OutboxDispatcherService.DispatchPendingAsync`.

Four event types raised:

- `SaleCreatedEvent` — on POST
- `SaleModifiedEvent` — on PUT
- `SaleCancelledEvent` — on PATCH `/cancel`
- `ItemCancelledEvent` — on PATCH `/items/{itemId}/cancel`

### Concurrency, idempotency, observability

- **Optimistic concurrency** on `Sale` via Postgres `xmin` (zero schema
  cost). Concurrent PUTs on the same sale produce 409.
- **`Idempotency-Key`** header on POST `/api/sales` replays the cached
  response for the same key/path for 24 hours. Backed by `IMemoryCache`
  for now — swap for Redis (already in `docker-compose.yml`) for
  multi-instance deployments.
- **Health probes**: `/health/live` (process), `/health/ready` (Postgres
  via `AddDbContextCheck`), `/health` (everything).
- **Structured logging** via Serilog (configurable per environment).
  Outbox dispatch and exception middleware emit structured logs.

### Running locally

```bash
cd template/backend

# 1. start the database (host port 5432:5432 is exposed)
docker compose up -d ambev.developerevaluation.database

# 2. apply migrations
dotnet ef database update \
  --project src/Ambev.DeveloperEvaluation.ORM \
  --startup-project src/Ambev.DeveloperEvaluation.WebApi

# 3. run the API
dotnet run --project src/Ambev.DeveloperEvaluation.WebApi
```

Swagger UI: `https://localhost:8081/swagger`.

#### Configuration

The default `appsettings.json` ships with **empty** `ConnectionStrings:DefaultConnection`
and `Jwt:SecretKey` — the app fails fast on startup if either is missing.
For local development, `appsettings.Development.json` already contains a
working Postgres connection string and a clearly-marked dev-only JWT key.

For production, set both via environment variables:

```bash
ConnectionStrings__DefaultConnection="Host=...;Port=5432;..."
Jwt__SecretKey="<at least 32 bytes>"
```

Or, for local development without committing secrets:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "..." \
  --project src/Ambev.DeveloperEvaluation.WebApi
dotnet user-secrets set "Jwt:SecretKey" "..." \
  --project src/Ambev.DeveloperEvaluation.WebApi
```

### Running the tests

```bash
cd template/backend
dotnet test
```

- **Unit tests** (`tests/Ambev.DeveloperEvaluation.Unit`): >100 tests
  covering the discount policy, the `Sale` aggregate invariants, the
  `CreateSaleValidator`, and every Sales handler (Create / Get / List /
  Update / Delete / Cancel / CancelSaleItem). No external dependencies.

- **Integration tests** (`tests/Ambev.DeveloperEvaluation.Integration`):
  end-to-end via `WebApplicationFactory` against a real Postgres running
  in a [Testcontainers](https://dotnet.testcontainers.org/) container.
  **Requires a working Docker daemon.** Boots once per fixture; tests
  share the container.

### Coverage report

```bash
cd template/backend
./coverage-report.sh   # or coverage-report.bat on Windows
open TestResults/CoverageReport/index.html
```

Uses [Coverlet](https://github.com/coverlet-coverage/coverlet) +
[ReportGenerator](https://reportgenerator.io/), both installed by the
script if missing.
