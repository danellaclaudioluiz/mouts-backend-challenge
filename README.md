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

See [Overview](/docs/spec/overview.md)

## Tech Stack
This section lists the key technologies used in the project, including the backend, testing, frontend, and database components. 

See [Tech Stack](/docs/spec/tech-stack.md)

## Frameworks
This section outlines the frameworks and libraries that are leveraged in the project to enhance development productivity and maintainability. 

See [Frameworks](/docs/spec/frameworks.md)

<!-- 
## API Structure
This section includes links to the detailed documentation for the different API resources:
- [API General](./docs/general-api.md)
- [Products API](/docs/spec/products-api.md)
- [Carts API](/docs/spec/carts-api.md)
- [Users API](/docs/spec/users-api.md)
- [Auth API](/docs/spec/auth-api.md)
-->

## Project Structure
This section describes the overall structure and organization of the project files and directories. 

See [Project Structure](/docs/spec/project-structure.md)

---

## Sales API

The Sales feature is the implementation of the use case described above —
a complete CRUD with quantity-tier discount rules, soft-cancel semantics,
and a transactional outbox dispatching domain events. Every endpoint
requires an authenticated user by default
(`AuthorizationFallbackPolicy = RequireAuthenticatedUser`); only
`POST /api/v1/auth`, `POST /api/v1/users` (self-service signup) and
the `/health/*` probes opt out via `[AllowAnonymous]`.

### CreateSale request lifecycle

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant API as SalesController
    participant IM as IdempotencyMiddleware
    participant H as CreateSaleHandler
    participant Sale as Sale aggregate
    participant Pub as IDomainEventPublisher
    participant DB as Postgres
    participant DSP as OutboxDispatcherService
    participant Brk as Broker / log sink

    C->>API: POST /api/v1/sales<br/>(Bearer, Idempotency-Key)
    API->>IM: pass through
    IM->>IM: SHA-256(body) → cache lookup
    alt cached 2xx response
        IM-->>C: replay 201 byte-equal
    else fresh request
        IM->>H: dispatch via MediatR
        H->>Sale: Sale.Create(header, items)
        Note over Sale: validate invariants;<br/>raise SaleCreatedEvent
        H->>Pub: PublishAsync(SaleCreatedEvent)
        Pub->>DB: stage OutboxMessage on context
        H->>DB: SaveChanges (sale + outbox in one tx)
        DB-->>DB: trigger NOTIFY outbox_pending
        DB-->>H: row count + RowVersion
        H-->>API: SaleDto
        API-->>IM: 201 + ETag + Location
        IM->>IM: cache body for 24h
        IM-->>C: 201
        DB->>DSP: NOTIFY wakes LISTEN connection
        DSP->>DB: SELECT FOR UPDATE SKIP LOCKED;<br/>set LockedUntil
        DSP->>Brk: DeliverAsync (envelope with eventId)<br/>(parallel across the batch)
        DSP->>DB: SET ProcessedAt = now()
    end
```

Two-phase dispatch keeps the row-lock window short: the SQL transaction
claims a batch and commits in milliseconds; the slow broker round-trip
runs **outside** the transaction so the lock isn't held while the
network is busy. Phase 2 delivers the batch in parallel (bounded
worker count). NOTIFY/LISTEN cuts the publish latency from "up to 5 s"
(the polling interval) to sub-second; the poll fallback handles
dropped notifications.

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
[`/docs/spec/general-api.md`](docs/spec/general-api.md): `_page`, `_size`, `_order`,
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

**Wire envelope.** Each `OutboxMessages.Payload` is a CloudEvents-flavoured
envelope, not the bare event:

```json
{
  "eventId":    "<uuid — same as OutboxMessages.Id>",
  "eventType":  "sale.created.v1",
  "occurredAt": "2026-05-10T23:11:50.987542Z",
  "data":       { "saleId": "…", "saleNumber": "…", "totalAmount": 45.00, … }
}
```

**At-least-once contract.** A dispatcher that crashes between successful
publish and the `ProcessedAt = now()` write will redeliver the message on
the next tick. Consumers MUST deduplicate by `eventId` — every Kafka /
RabbitMQ / SNS subscriber on the receiving end is expected to keep a
short-window seen-set of recent `eventId`s (Redis SET with TTL, or a
window table) and discard repeats. Without that, downstream side-effects
will fire twice on every dispatcher hiccup. The `eventId` is also the
correlation handle for ops: a single id traces a message from
`OutboxMessages` to the broker log to the consumer's processed-events
table.

**Auto-migration & multi-pod rollouts.** The app ships migrations via
`dotnet ef database update` at deploy time — `Database.MigrateAsync()` is
NOT called at startup. If a future iteration adds it, wrap the call in a
Postgres advisory lock so concurrent rolling-deploy pods do not race the
schema:

```csharp
await using (var conn = new NpgsqlConnection(connString))
{
    await conn.OpenAsync();
    // pg_advisory_lock blocks (cooperatively) until any other holder
    // releases — exactly one pod runs MigrateAsync, the rest wait, see
    // the schema is up-to-date, and return cleanly.
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT pg_advisory_lock(8945312094);";
        await cmd.ExecuteNonQueryAsync();
    }
    await context.Database.MigrateAsync();
    // Advisory locks are released by session close (await using above).
}
```

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
  429 when exhausted. A separate `auth-strict` policy caps `POST /auth`
  at 5 req/min/IP to slow password brute-force.
- **Authorization**: every endpoint requires an authenticated user via
  `AuthorizationFallbackPolicy = RequireAuthenticatedUser`. Only
  `POST /auth` (login), `POST /users` (self-service signup), and the
  `/health/*` probes opt out via `[AllowAnonymous]`. Self-service signup
  hard-codes `role=Customer` + `status=Active` in the handler so a
  smuggled-in `role: "Admin"` body field cannot escalate privileges.
- **JWT hardening**: HS256 with a ≥32-byte signing key. `Jwt:Issuer`
  and `Jwt:Audience` are mandatory outside Development so a leaked key
  in one service cannot mint tokens accepted by another.
- **Transport hardening**: `RequireHttpsMetadata = !IsDevelopment()`,
  `UseHsts()` outside dev (1y, includeSubDomains), security headers
  middleware adds `X-Content-Type-Options: nosniff`, `X-Frame-Options:
  DENY`, `Referrer-Policy: no-referrer`, and a `default-src 'none'` CSP
  (JSON API never serves HTML).
- **Login timing & enumeration**: login responses use a single
  "Invalid credentials" message and run BCrypt verification even when
  the email is unknown (constant-time path via a frozen dummy hash) so
  response latency does not double as a user-enumeration oracle.
- **Forwarded headers** (`X-Forwarded-For`, `X-Forwarded-Proto`) are
  honoured **only** from configured `ForwardedHeaders:KnownProxies` /
  `KnownNetworks` so an attacker cannot spoof their IP to poison logs
  or the rate-limit partition. Loopback is allowed in Development/Test.
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

### Known limitations (documented gaps)

- **Outbox dispatcher publishes to a structured log only.** A real
  broker (Kafka / RabbitMQ / SNS) is out of scope for the challenge;
  the dispatcher's `DeliverAsync` ships a single, swappable seam an
  operator can replace with `IDomainEventBroker` for production.
- **Previously known PUT-replace-items 409 (resolved).** An earlier
  pass tracked a `MissingScenarioTests.UpdateSale_ReplacesAllItems_Skipped`
  test because PUT requests that introduced new `productId`s
  intermittently returned 409 from EF's optimistic-concurrency check.
  The root cause turned out to be EF Core 8's new-entity detection
  heuristic: aggregate-assigned `Guid` PKs (`Sale.Id`, `SaleItem.Id`
  are set in the constructor via `Guid.NewGuid()`) were treated as
  "entity already exists", so freshly-added child rows came out as
  `UPDATE WHERE Id=…` (matching zero rows) instead of `INSERT`.
  Fixed by `Property(s => s.Id).ValueGeneratedNever()` on both
  `SaleConfiguration` and `SaleItemConfiguration`; the integration
  test `UpdateSale_ReplacesAllItems` (no Skip) now covers the
  full-replace path end-to-end.

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
# 1. start the database (port 5432 NOT published by default — the
#    docker-compose.override.yml shipped in this repo binds it to
#    127.0.0.1:5432 for local dev so the host's `dotnet ef` /
#    `dotnet run` can reach it).
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

The default `appsettings.json` ships with **empty** secrets — the app
fails fast on startup if `ConnectionStrings:DefaultConnection` or
`Jwt:SecretKey` is missing. Secrets are externalised; pick one source:

- **`docker compose up`** — copy `.env.example` to
  `.env` (gitignored), fill in your values, and `docker compose up`.
  Compose refuses to start if any required variable is unset.
- **`dotnet user-secrets`** — for `dotnet run` on the host. Set with
  `dotnet user-secrets set ConnectionStrings:DefaultConnection "..."`.
- **Environment variables** — `ConnectionStrings__DefaultConnection`,
  `Jwt__SecretKey`, `Jwt__Issuer`, `Jwt__Audience`,
  `Swagger__Enabled`, `ForwardedHeaders__KnownProxies`,
  `ForwardedHeaders__KnownNetworks`, `RateLimit__PermitLimit`,
  `RateLimit__WindowSeconds`, `RateLimit__AuthPermitLimit`.

In production, `Jwt:Issuer` and `Jwt:Audience` are mandatory — startup
fails with a clear error if either is unset, preventing a leaked
signing key from being reused across services.

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
# Unit + integration (integration needs Docker for Testcontainers Postgres):
dotnet test

# Or only one of them:
dotnet test tests/Ambev.DeveloperEvaluation.Unit
dotnet test tests/Ambev.DeveloperEvaluation.Integration
```

For an end-to-end smoke against a running API (15 sections, 96 curl
assertions covering auth wall, mass-assignment defence, idempotency
replays, ETag/If-Match, outbox dispatch, XSS round-trip, rate limit):

```bash
bash scripts/smoke.sh           # BASE=http://localhost:5119 by default
```

- **Unit tests** (`tests/Ambev.DeveloperEvaluation.Unit`): >100 tests
  covering the discount policy, the `Sale` aggregate invariants, the
  `CreateSaleValidator`, and every Sales handler (Create / Get / List /
  Update / Delete / Cancel / CancelSaleItem). No external dependencies.

- **Integration tests** (`tests/Ambev.DeveloperEvaluation.Integration`):
  end-to-end via `WebApplicationFactory` against a real Postgres running
  in a [Testcontainers](https://dotnet.testcontainers.org/) container.
  **Requires a working Docker daemon.** Boots once per fixture; tests
  share the container and reset per-test via `TRUNCATE … RESTART
  IDENTITY CASCADE` so each `[Fact]` starts from a known-empty schema.

#### Integration test coverage matrix

| Area | File | What it asserts |
|---|---|---|
| Happy paths + ETag/Location | `SalesEndpointsTests.cs` | POST returns 201 + `Location` + `ETag`, GET returns 404 problem details, validation errors return 400 problem details, PATCH `/cancel` toggles `IsCancelled` + writes a `sale.cancelled.v1` outbox row, stale `If-Match` returns 412 |
| Idempotency-Key | `SalesEndpointsTests.cs` | Replay returns cached 201 byte-equal to the original, different body returns 422, whitespace + key-order variants share the canonical hash, 4xx responses are not cached, key > 256 chars returns 400 |
| Concurrency races | `ConcurrencyTests.cs` | Two PUTs with the same stale `If-Match` → exactly one 200 + one 412/409 and the DB reflects the winner only; 5 concurrent POSTs with the same `Idempotency-Key` → exactly one `Sales` row regardless of inflight-lock outcome |
| Update | `UpdateSaleEndpointTests.cs` | Happy path + diff-style update keeps stable item ids, update against a cancelled sale returns 400, unknown id returns 404 |
| Delete | `DeleteSaleEndpointTests.cs` | Hard-delete cascades items, current `If-Match` succeeds, stale `If-Match` returns 412, unknown id returns 404 |
| Cancel item | `CancelSaleItemEndpointTests.cs` | Item flagged cancelled + total recalculated, second cancel on same item is idempotent (no extra event), unknown sale → 404, unknown item → 400 |
| List + pagination + filters | `ListSalesEndpointTests.cs` | Page/size paging, customer/branch/`isCancelled` filters, ordering, bad order key → 400, oversize page → 400, empty page is well-formed, keyset cursor mode, `_page` + `_cursor` together → 400 |
| Boundaries | `BoundaryEndpointTests.cs` | Exactly `MaxItemsPerSale` (100) items accepted, 101 items → 400, duplicate `productId` across lines → 400 (cap cannot be split) |
| Rate limit | `RateLimitEndpointTests.cs` | Dedicated factory with `RateLimit:PermitLimit=5`: bursts of requests beyond the permit return 429 |
| Health | `HealthEndpointTests.cs` | `/health/live` returns Healthy, `/health/ready` includes the Postgres DB probe, `/health` returns the full report; stopping the testcontainer flips `/health/ready` to 503 |
| Outbox lifecycle | `OutboxLifecycleTests.cs` | Poison-pill quarantine at `Attempts >= 10`, 30-day retention DELETE via the real `OutboxCleanupService.CleanupOnceAsync` (`InternalsVisibleTo`), envelope-shape sanity |
| Authorization wall | `AuthorizationEndpointTests.cs` | Anonymous on every protected Sales route returns 401 (GET/POST/PUT/DELETE/PATCH-cancel/PATCH-items-cancel); login + signup + health stay reachable; mass-assignment defence (smuggled `role: "Admin"` on signup falls back to Customer) |
| Missing scenarios | `MissingScenarioTests.cs` | Cancel-already-cancelled HTTP idempotency, PUT with empty items returns 400 with `Items` in `errors{}`, PUT replacing all items with brand-new productIds (no Skip), multi-key `_order` tie-breaker, combined filters (customerId + date range + isCancelled) AND-ed |
| If-Match contract | `IfMatchEndpointTests.cs` | `If-Match: *` bypasses precondition; `If-Match: ""` treated as absent; stale value returns 412 |
| Abuse vectors | `AbuseEndpointTests.cs` | XSS round-trip (`<script>` stored verbatim, JSON-escaped on output), 51-char SaleNumber/201-char names → 400, malformed GUID → 404 (route constraint), unknown `_api_version` returns 400/404 not 500 |
| Production parity | `JwtProductionParityTests.cs` | Dedicated factory running under "Staging" env (iss/aud mandatory): generated token carries `iss`, `aud`, `jti`, `iat`; signup → login → authenticated GET round-trips. Regression guard for CN-2 (generator omitting iss/aud → 401 storm in prod) |
| Outbox side-effects (helper) | `Helpers/OutboxAsserter.cs` | Reads rows directly, asserts `EventType` alias and deserialises the envelope's `data` block into a typed payload — used by every test that touches the outbox |

#### Unit test coverage matrix

| Area | File | What it asserts |
|---|---|---|
| Discount policy tiers | `Domain/Services/SaleItemDiscountPolicyTests.cs` | Documented quantity tiers (0% / 10% / 20%), > 20 throws, non-positive qty/price throws, tier borders × awkward unit prices (0.01, 33.33, 999.99) match the rounding contract (AwayFromZero, 2 dp) |
| Sale aggregate | `Domain/Entities/SaleTests.cs` | AddItem rejects duplicate product (any price/name), AddItem on cancelled sale throws, Cancel is idempotent + emits a single event, CancelItem recalculates total, unknown CancelItem throws, cancel-cancelled-item is a no-op, cancel on empty sale stays consistent, Cancel cascades to all active items |
| Sale items | `Domain/Entities/SaleItemTests.cs` | Line total = `qty × price - discount` across every tier |
| Validators | `Application/Sales/.../CreateSaleValidatorTests.cs` etc. | Required fields, length caps, date bounds, items-array cap |
| Handlers | `Application/Sales/.../{UseCase}HandlerTests.cs` | Repository + event publisher are called with the right shape; duplicate `SaleNumber` raises `ConflictException`; missing aggregate raises `ResourceNotFoundException` |

### Continuous integration

`.github/workflows/ci.yml` runs **six jobs**. Five fire on every push
and pull request — including the vulnerability gate (`supply-chain`),
which must fail fast in PR review. Only `mutation-testing` is gated
behind nightly + `workflow_dispatch`, because a single Stryker pass
takes minutes per file.

| Job | Trigger | What it does |
|---|---|---|
| `build-test` | push, PR | `dotnet restore` → `build` (Release) → `dotnet format --verify-no-changes` → unit tests with Coverlet + Codecov upload |
| `integration-test` | push, PR | Testcontainers Postgres suite; ubuntu-latest already ships a Docker daemon |
| `migration-validate` | push, PR | `dotnet ef migrations script --idempotent` against the ORM project; fails if the rendered SQL is empty (catches a migration that references a model that no longer compiles) and uploads the script as an artifact |
| `supply-chain` | push, PR | `dotnet list package --vulnerable --include-transitive` fails the PR on `Critical`; CycloneDX SBOM is generated and uploaded as an artifact for downstream provenance |
| `mutation-testing` | nightly + manual | Stryker.NET against `Domain` + `Application` ([`stryker-config.json`](stryker-config.json)), thresholds high 85 / low 70 / break 60. HTML report uploaded as an artifact |
| `secret-scan` | push, PR | `gitleaks/gitleaks-action` on the full history (`fetch-depth: 0`) |

[`.github/dependabot.yml`](.github/dependabot.yml) watches NuGet
(grouped by Microsoft runtime / OpenTelemetry / test tooling), GitHub
Actions, and the WebApi Dockerfile — weekly PR cadence, max 10
open at a time.

### Coverage report

```bash
# From the repo root:
bash scripts/coverage-report.sh        # macOS / Linux / git-bash
scripts/coverage-report.bat            # Windows cmd.exe
open TestResults/CoverageReport/index.html
```

Uses [Coverlet](https://github.com/coverlet-coverage/coverlet) +
[ReportGenerator](https://reportgenerator.io/), both installed by the
script if missing.

### Mutation testing (Stryker.NET)

Coverage tells you which lines ran; mutation testing tells you whether
the tests would notice if those lines were wrong. CI runs Stryker
nightly + on `workflow_dispatch`. Run it locally:

```bash
dotnet tool install --global dotnet-stryker
dotnet stryker --config-file stryker-config.json --reporter cleartext --reporter html
open StrykerOutput/<timestamp>/reports/mutation-report.html
```

Scope is `Domain` + `Application` (ORM / WebApi mutations are mostly
noise on a portfolio-sized suite); thresholds are high 85 / low 70 /
break 60 — see [`stryker-config.json`](stryker-config.json) for the
rationale.

---

## Engineering documentation

| Document | What it covers |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | Layered DDD, MediatR pipeline, transactional outbox, CreateSale sequence diagram |
| [`docs/api.md`](docs/api.md) | Every endpoint, ProblemDetails contract, ETag/If-Match, Idempotency-Key, status codes |
| [`docs/database.md`](docs/database.md) | Schema, indexes (composite / partial / GIN trgm), CHECK constraints, RowVersion trigger, migrations |
| [`docs/security.md`](docs/security.md) | AuthN/AuthZ posture, OWASP Top 10 mapping, audit history, JWT rotation runbook anchor |
| [`docs/devops.md`](docs/devops.md) | Docker, compose, health checks, observability, CI job table, troubleshooting |
| [`docs/testing.md`](docs/testing.md) | Unit + integration matrix, fixtures, Stryker, smoke script |
| [`docs/runbook.md`](docs/runbook.md) | 8 on-call scenarios: DB down, outbox backlog, JWT leak, Redis degraded, rate-limit storm, idempotency replays, security incident, queries |
| [`docs/challenge-brief.md`](docs/challenge-brief.md) | Verbatim copy of the original challenge statement |
| [`docs/spec/`](docs/spec/) | Original template spec (overview, tech stack, frameworks, project structure, general-api, users-api, products-api, carts-api, auth-api) |
