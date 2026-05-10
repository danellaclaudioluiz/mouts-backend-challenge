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
| `POST` | `/api/v1/sales` | Create a sale (header + items). Honours `Idempotency-Key`. |
| `GET` | `/api/v1/sales/{id}` | Get a sale by id (full body with items). |
| `GET` | `/api/v1/sales` | List sales (paginated, filtered, ordered) — header-only summaries. |
| `PUT` | `/api/v1/sales/{id}` | Diff-based update: existing items keep their id when only quantity/price changes. |
| `DELETE` | `/api/v1/sales/{id}` | Hard-delete a sale and its items (cascade). |
| `PATCH` | `/api/v1/sales/{id}/cancel` | Soft-cancel a sale (idempotent). |
| `PATCH` | `/api/v1/sales/{id}/items/{itemId}/cancel` | Cancel a single line and recalculate the total. |

The list endpoint follows the conventions in
[`/.doc/general-api.md`](.doc/general-api.md): `_page`, `_size`, `_order`,
plus `_minDate` / `_maxDate`, `customerId`, `branchId`, `isCancelled`,
`saleNumber` (substring with `*`).

It also supports **keyset (cursor) pagination** for high-scale clients via
the `_cursor` query parameter. The response carries `nextCursor` when more
pages exist; pass it back as `_cursor=...` on the next request and the
query runs in O(log n) per page with no COUNT(\*) round-trip. `_cursor`
and `_page` are mutually exclusive — pick one mode per call.

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

Per product, across non-cancelled lines. Each `ProductId` may appear
**at most once** in a sale's items — callers consolidate before sending,
which keeps Create and Update consistent and makes the 20-cap
unbypassable by splitting lines.

| Quantity (per product) | Discount |
|---|---|
| 1–3 | 0% |
| 4–9 | 10% |
| 10–20 | 20% |
| above 20 | not allowed (HTTP 400) |

### Domain events & outbox

Each Sales handler stages events into an `OutboxMessages` table inside the
same transaction that persists the aggregate. A hosted background service
(`OutboxDispatcherService`) polls the table every 5 seconds with a
`SELECT … FOR UPDATE SKIP LOCKED` so multiple instances cooperate without
duplicating dispatches, marks each row processed only **after** a
successful publish (at-least-once semantics), and tracks attempt count +
last error per row for retry visibility.

Events use stable wire aliases (`[EventType("…")]`) decoupled from the
CLR type, so a class rename or namespace move does not invalidate enqueued
rows or downstream consumers:

| Event class | Wire alias |
|---|---|
| `SaleCreatedEvent` | `sale.created.v1` |
| `SaleModifiedEvent` | `sale.modified.v1` |
| `SaleCancelledEvent` | `sale.cancelled.v1` |
| `ItemCancelledEvent` | `sale.item_cancelled.v1` |

### Concurrency, idempotency, observability

- **API versioning**: routes live at `/api/v{version}/…` via
  `Asp.Versioning.Mvc`. Default version is 1.0 and the
  `api-supported-versions` response header reports the available range.
- **Optimistic concurrency** on `Sale` via a `bigint RowVersion` column
  maintained by a Postgres BEFORE UPDATE trigger
  (`ambev_sales_bump_rowversion`). Monotonic per row and unaffected by
  VACUUM FREEZE — concurrent PUTs on the same sale produce 409.
- **HTTP `ETag` / `If-Match`**: `GET /api/v1/sales/{id}` returns an `ETag`
  derived from the row version. `POST`/`PUT`/`PATCH` also emit `ETag` so
  clients can chain writes without an extra `GET`. `PUT` and `DELETE`
  accept `If-Match`; a stale value returns 412 Precondition Failed.
- **`Idempotency-Key`** header on POST replays only successful (2xx)
  responses, fingerprinted with a SHA-256 of the request body — reusing
  the same key with a different body returns 422. Backed by
  `IDistributedCache`: Redis when `ConnectionStrings:Redis` is set
  (multi-pod safe), in-memory fallback otherwise.
- **2nd-level read cache** (`ISaleReadCache`) in front of
  `GET /api/v1/sales/{id}` — same `IDistributedCache` (Redis if
  configured), 60-second TTL safety net, evicted explicitly on every
  write (Update / Cancel / CancelItem / Delete) so the next read sees
  the new state immediately.
- **Health probes**: `/health/live` (process), `/health/ready` (Postgres
  via `AddDbContextCheck`), `/health` (full report).
- **CORS** with restrictive default; configure allowed origins via
  `Cors:AllowedOrigins`.
- **Rate limiting**: 100 requests per minute per principal (claim id
  first, IP fallback) on the controller surface (fixed-window). Returns
  429 when exhausted.
- **Structured logging** via Serilog enriched with TraceId/SpanId so
  log lines join their OpenTelemetry traces.
- **OpenTelemetry** traces (ASP.NET Core, HTTP client, EF Core) and
  metrics (ASP.NET Core, HTTP client, runtime). Exports via OTLP when
  `OpenTelemetry:OtlpEndpoint` (or `OTEL_EXPORTER_OTLP_ENDPOINT`) is set.

### Database tuning

The schema and connection setup are tuned for production-style load:

- **DbContext is pooled** (`AddDbContextPool`, poolSize 256) with EF
  Core's `EnableRetryOnFailure(3, 2s)` and a 15-second command timeout.
  The connection string declares
  `Pooling=true; Minimum Pool Size=10; Maximum Pool Size=200;
  Keepalive=30` so brief network blips and PG failovers don't cascade
  into 5xx.
- **Indexes** matched to query patterns:
  - `Users.Email` and `Users.Username` are unique (auth path goes
    through a btree index, not a seq scan).
  - `Sales(CustomerId, SaleDate)` and `Sales(BranchId, SaleDate)`
    composite for the listing patterns.
  - Two partial indexes on `Sales(SaleDate)` — one for active rows
    (`WHERE IsCancelled = false`), one for cancelled — keep both flavours
    of the listing fast without bloating a single index.
  - `OutboxMessages(OccurredAt) WHERE ProcessedAt IS NULL` is a real
    partial index, so the dispatcher's hot-path tree stays small even
    after months of dispatched rows.
  - `SaleItems(SaleId, ProductId) UNIQUE WHERE NOT IsCancelled`
    encodes the aggregate's per-product invariant at the database level.
  - `pg_trgm` GIN index on `Sales.SaleNumber` so substring filters
    (`%foo%`) use the index instead of seq-scanning.
- **CHECK constraints** on `SaleItems`: `Quantity` ∈ [1, 20],
  `UnitPrice > 0`, `Discount ≥ 0`, `TotalAmount ≥ 0` — defence-in-depth
  against bypassing the aggregate via raw SQL.
- **Outbox cleanup** runs in 5,000-row chunks, each in its own short
  transaction, so autovacuum keeps up and WAL doesn't spike. The
  dispatcher dead-letters messages after 10 failed attempts (the row
  stays in the table with `LastError` populated for inspection).
- **Outbox dispatch is two-phase** (claim with `LockedUntil`, publish
  outside any transaction, mark in a short second transaction) so a slow
  broker round-trip never holds row locks. It also keeps a dedicated
  LISTEN connection on the `outbox_pending` channel — a trigger fires
  `NOTIFY` on every outbox insert, so end-to-end publish latency drops
  from "up to 5 s" (poll interval) to sub-second under load.
- **Compiled queries** (`EF.CompileAsyncQuery`) for the two hottest
  reads: `Sale.GetByIdAsync` (every write path hits it) and
  `User.GetByEmailAsync` (every authn request).
- **Cheap pre-checks**: `SaleNumberExistsAsync` and `EmailExistsAsync`
  use `AsNoTracking() + AnyAsync()` for duplicate-check on Create. The
  unique index is still the source of truth — a concurrent insert that
  slips past returns 409 from the middleware.

### Known future work (not in scope for the challenge)

- **`pg_trgm` GIN index on `OutboxMessages.Payload`** — only worth
  adding the day someone needs to query messages by JSON content (e.g.
  "all events for customerId X"). Today the table is append-and-read-by-id
  for the dispatcher; a `USING gin (Payload jsonb_path_ops)` plus a
  query rewrite covers that future need with a small storage cost.
- **Partition `OutboxMessages` by month** (declarative range partitions
  on `OccurredAt`) so the cleanup job becomes `DROP PARTITION` instead
  of a chunked DELETE — instantaneous, no autovacuum churn.
- **Compiled bulk-update plans for the dispatcher** (skip the per-row
  failure UPDATE in favour of a CTE-based batched UPDATE).
- **2nd-level cache for `ListSales`** with cache-aside invalidation on
  any sale write (today only the by-id read is cached).

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

# Optional, but recommended in multi-pod deployments
ConnectionStrings__Redis="redis-host:6379"
Cors__AllowedOrigins__0="https://app.example.com"
OTEL_EXPORTER_OTLP_ENDPOINT="https://otel-collector.internal:4317"
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

### Continuous integration

`.github/workflows/ci.yml` runs three jobs on every push and pull request:

- **build-test** — `dotnet restore`, `dotnet build` (Release),
  `dotnet format --verify-no-changes`, and the unit test suite with
  Coverlet collection. Test results are uploaded as a workflow artifact.
- **integration-test** — runs the Testcontainers Postgres suite. The
  GitHub-hosted `ubuntu-latest` runner ships with a Docker daemon, so no
  extra services are needed.
- **secret-scan** — `gitleaks/gitleaks-action` on the full history.

### Coverage report

```bash
cd template/backend
./coverage-report.sh   # or coverage-report.bat on Windows
open TestResults/CoverageReport/index.html
```

Uses [Coverlet](https://github.com/coverlet-coverage/coverlet) +
[ReportGenerator](https://reportgenerator.io/), both installed by the
script if missing.
