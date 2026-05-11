# Architecture

A pragmatic, layered DDD slice. Six projects, hard dependencies in one
direction (outer → inner), aggregate-rooted writes, MediatR everywhere
between the controller and the repository.

[← back to README](../README.md)

---

## Project graph

```
                ┌─────────────────────────┐
                │         WebApi          │  Controllers, middleware,
                │  (ASP.NET host, DI)     │  Program.cs, Dockerfile
                └────────────┬────────────┘
                             │ depends on
                             ▼
                ┌─────────────────────────┐
                │           IoC           │  Module initializers — the
                │ (composition root only) │  only thing that wires
                └────────────┬────────────┘  Application + ORM into the host
                             │
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
       ┌───────────┐  ┌───────────┐  ┌───────────┐
       │ Application│  │    ORM    │  │  Common   │
       │  (MediatR │  │  (EF Core │  │ (cross-   │
       │  handlers,│  │  context, │  │ cutting:  │
       │  DTOs,    │  │  outbox,  │  │ auth,     │
       │  mapping) │  │  repos)   │  │ health,   │
       └─────┬─────┘  └─────┬─────┘  │ logging,  │
             │              │        │ validation)│
             ▼              ▼        └─────┬─────┘
       ┌─────────────────────────┐         │
       │         Domain          │ ◄───────┘
       │   (aggregates, events,  │
       │  exceptions, contracts) │
       └─────────────────────────┘
```

- `Domain` has zero project references. No EF Core, no ASP.NET, no
  AutoMapper. Pure C#.
- `Application` references `Domain` and `Common` only — MediatR /
  FluentValidation / AutoMapper are infrastructure-agnostic.
- `ORM` references `Domain` + `Application` (for the `ISaleReadCache`
  abstraction it implements). It hosts the EF context, mappings,
  migrations, outbox publisher, and the dispatcher / cleanup
  background services.
- `WebApi` references everything; `IoC` is the only project that knows
  about *all* the layers and wires them in `Program.cs`.
- `Common` is leaf-ish: just cross-cutting helpers (JWT, BCrypt,
  health-check endpoint conventions, the FluentValidation pipeline
  behaviour). It does not reference Domain.

Project files: see the solution at
[Ambev.DeveloperEvaluation.sln](../Ambev.DeveloperEvaluation.sln).

---

## Layer responsibilities

| Layer | Owns | Doesn't own |
|---|---|---|
| **Domain** | Aggregates (`Sale`, `SaleItem`, `User`), invariants, domain events, repository **contracts**, domain exceptions, the discount policy | Persistence, HTTP, validation messages, framework dependencies |
| **Application** | Use cases as MediatR `IRequest`/`Handler` pairs, DTOs, FluentValidation `Validator`s, AutoMapper profiles, the `IDomainEventPublisher` contract | EF Core, controllers, raw SQL |
| **ORM** | `DefaultContext`, EF mappings (one per aggregate), migrations, `SaleRepository`/`UserRepository`, the outbox table + dispatcher + cleanup, the LISTEN/NOTIFY trigger | Use cases, request validation, HTTP |
| **WebApi** | Controllers, middleware (idempotency, exception → ProblemDetails), security headers, rate limiting, CORS, OpenTelemetry wiring, health endpoints | Business logic, repository details |
| **IoC** | One initializer per layer (`ApplicationModuleInitializer`, `InfrastructureModuleInitializer`, `WebApiModuleInitializer`) — the only seam aware of all three | Anything else |
| **Common** | JWT generator + auth extension, BCrypt hasher, FluentValidation pipeline behaviour, health-check helpers, Serilog bootstrap | Domain concepts |

---

## Request lifecycle: `POST /api/v1/sales`

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
        DSP->>Brk: DeliverAsync (envelope with eventId)
        DSP->>DB: SET ProcessedAt = now()
    end
```

Two-phase dispatch keeps the row-lock window short: the SQL transaction
claims a batch and commits in milliseconds; the slow broker round-trip
runs **outside** the transaction so the lock isn't held while the
network is busy. NOTIFY/LISTEN cuts the publish latency from "up to 5 s"
(the polling interval) to sub-second; the poll fallback handles dropped
notifications.

End-to-end path through the layers. File:line references are below
each step.

1. **HTTP enters the pipeline.** Forwarded headers normalised, security
   headers attached, CORS evaluated, rate limit checked, JWT validated,
   `[AllowAnonymous]` / `FallbackPolicy = RequireAuthenticatedUser`
   decides whether the call proceeds.
   [`Program.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Program.cs)
2. **Idempotency middleware** inspects the `Idempotency-Key` header
   (POST only). On a hit, the cached 2xx body is replayed and the
   pipeline stops. On a miss, a short-lived in-flight lock is taken to
   serialise concurrent requests with the same key.
   [`IdempotencyMiddleware.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Middleware/IdempotencyMiddleware.cs)
3. **Routing → `SalesController.CreateSale`** binds the
   `CreateSaleRequest` and AutoMapper projects it into a
   `CreateSaleCommand`.
   [`SalesController.cs:35`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Sales/SalesController.cs#L35)
4. **MediatR ValidationBehavior** runs FluentValidation against the
   command. Any failure throws `ValidationException` which is converted
   to RFC 7807 by the exception middleware.
   [`ValidationBehavior.cs`](../src/Ambev.DeveloperEvaluation.Common/Validation/ValidationBehavior.cs),
   [`CreateSaleValidator.cs`](../src/Ambev.DeveloperEvaluation.Application/Sales/CreateSale/CreateSaleValidator.cs)
5. **`CreateSaleHandler`** runs the cheap pre-check
   (`SaleNumberExistsAsync`), then constructs the aggregate via the
   `Sale.Create` factory — domain rules execute as the items are added
   (per-product uniqueness, 20-quantity cap, tiered discount).
   [`CreateSaleHandler.cs`](../src/Ambev.DeveloperEvaluation.Application/Sales/CreateSale/CreateSaleHandler.cs),
   [`Sale.cs`](../src/Ambev.DeveloperEvaluation.Domain/Entities/Sale.cs)
6. **Domain events staged.** The aggregate's `MarkCreated()` emits a
   `SaleCreatedEvent`. The handler hands every event in
   `sale.DomainEvents` to `IDomainEventPublisher.PublishAsync`, whose
   `OutboxDomainEventPublisher` implementation INSERTs an
   `OutboxMessages` row into the same EF `ChangeTracker` instance —
   guaranteeing it commits in the same transaction as the aggregate.
   [`OutboxDomainEventPublisher.cs`](../src/Ambev.DeveloperEvaluation.ORM/Outbox/OutboxDomainEventPublisher.cs)
7. **Persist.** `SaleRepository.CreateAsync` calls `SaveChangesAsync`.
   The Postgres trigger `ambev_sales_bump_rowversion` fires BEFORE
   UPDATE on `Sales` and the trigger `ambev_outbox_notify` fires AFTER
   INSERT on `OutboxMessages`, raising `NOTIFY outbox_pending`.
8. **Response shaped.** The handler maps the saved aggregate to
   `SaleDto`, the controller stamps an `ETag` header derived from the
   row version, and returns `201 Created` with `Location:
   /api/v1/sales/{id}`.
9. **Idempotency middleware caches** the response body (24h TTL) keyed
   by `Idempotency-Key + canonical body hash`.
10. **Outbox dispatcher**, woken by `NOTIFY outbox_pending` (sub-second)
    or by its 5-second poll, claims the row under a soft lock and calls
    `DeliverAsync` (logs the CloudEvents-flavoured envelope today; would
    publish to a broker in production). On success it sets
    `ProcessedAt`; on failure it bumps `Attempts` and stamps
    `LastError`, dead-letters at 10 failed attempts.
    [`OutboxDispatcherService.cs`](../src/Ambev.DeveloperEvaluation.ORM/Outbox/OutboxDispatcherService.cs)

---

## Aggregate design

The `Sale` aggregate root is the **only** place where item-level state
can change. The internal `List<SaleItem>` is exposed as a read-only
collection; every mutation goes through `Sale.AddItem`,
`Sale.UpdateItem`, `Sale.RemoveItem`, `Sale.Cancel`, or
`Sale.CancelItem`. This is what makes the per-product uniqueness rule
("a `ProductId` appears at most once in non-cancelled items") a true
invariant rather than a controller-level check — the cap of 20 cannot
be bypassed by splitting a single product across two lines.

The `External Identities` pattern is preserved literally:
`CustomerId/CustomerName` and `BranchId/BranchName` are stored
denormalised, with no foreign keys to other aggregates. Updates to
those names elsewhere do not propagate; a sale captures the
customer/branch identity at the time it was created.

Item totals (`Quantity × UnitPrice − Discount`) are calculated by the
domain on every mutation through
[`SaleItemDiscountPolicy.Calculate`](../src/Ambev.DeveloperEvaluation.Domain/Services/SaleItemDiscountPolicy.cs).
The same policy lives behind a Postgres `CK_SaleItems_Quantity` CHECK
constraint, so a future caller bypassing the aggregate (raw SQL, ad-hoc
migration) still cannot persist 21 of the same item.

Cancellation is **soft**: `Sale.Cancel()` flips `IsCancelled`,
cascades to active items, recalculates `TotalAmount` to 0, and raises
`SaleCancelledEvent` exactly once (re-cancel is a no-op,
**idempotent**). `CancelItem` is also idempotent.

---

## Domain events & transactional outbox

The challenge allows logging events instead of publishing to a broker.
This implementation goes one step further and uses a **transactional
outbox** so the message lifecycle is correct end-to-end, with a single
swappable seam standing in for the broker.

### Why an outbox

Without one, two failures are possible:

1. The aggregate commits but the broker publish throws → event is lost.
2. The broker publish succeeds but the DB commit fails → event is fake.

The transactional outbox closes both: the event is INSERTed in the same
EF `SaveChangesAsync` as the aggregate, so either both land or neither.
A background dispatcher reads pending rows and publishes them
asynchronously.

### Wire format

Every `OutboxMessages.Payload` is a CloudEvents-flavoured envelope —
not the bare event:

```json
{
  "eventId":    "<uuid — same as OutboxMessages.Id>",
  "eventType":  "sale.created.v1",
  "occurredAt": "2026-05-10T23:11:50.987542Z",
  "data":       { "saleId": "…", "saleNumber": "…", "totalAmount": 45.00, "...": "..." }
}
```

CLR class names map to **stable wire aliases** via `[EventType("…")]`
so a class rename or namespace move does not invalidate enqueued rows
or downstream consumers:

| Event class | Wire alias |
|---|---|
| `SaleCreatedEvent` | `sale.created.v1` |
| `SaleModifiedEvent` | `sale.modified.v1` |
| `SaleCancelledEvent` | `sale.cancelled.v1` |
| `ItemCancelledEvent` | `sale.item_cancelled.v1` |

Event sources:
[Domain/Events/](../src/Ambev.DeveloperEvaluation.Domain/Events/).

### Dispatcher mechanics

[`OutboxDispatcherService`](../src/Ambev.DeveloperEvaluation.ORM/Outbox/OutboxDispatcherService.cs)
is a `BackgroundService` that:

1. Opens a dedicated `LISTEN outbox_pending` connection on startup
   (separate from EF's pool).
2. Waits for either a `NOTIFY` (immediate) or a 5-second poll tick
   (safety net), with ±200 ms jitter to prevent thundering herds.
3. **Phase 1 (short tx):** `SELECT … FOR UPDATE SKIP LOCKED` a batch
   of up to 50 unprocessed rows, stamps `LockedUntil = now() + 30s`,
   commits. Other replicas skip locked rows.
4. **Phase 2 (no tx):** publishes each message via `DeliverAsync` —
   the only place that touches the broker / network. No DB locks are
   held during the slow part.
5. **Phase 3 (short tx):** bulk-marks success
   (`ProcessedAt`, `LastError = NULL`); per-row updates failures with
   `LastError` truncated to 2000 chars. After 10 failures the row is
   not picked up again ("dead-letter": stays visible in the table for
   inspection, never auto-purged).

`BackgroundServiceExceptionBehavior.Ignore` is set in `Program.cs` so
a DB outage in any hosted service can't tear down the API host — the
service's own try/catch logs and retries; `/health/ready` surfaces the
DB outage independently.

### At-least-once semantics

A dispatcher that crashes between a successful publish and the
`ProcessedAt = now()` write redelivers on the next tick. **Consumers
MUST deduplicate by `eventId`** (Redis set with TTL, or a windowed
processed-events table). The `eventId` is also the operator's
correlation handle — one id traces a message from `OutboxMessages` to
the broker log to the consumer's processed-events table.

### Cleanup

[`OutboxCleanupService`](../src/Ambev.DeveloperEvaluation.ORM/Outbox/OutboxCleanupService.cs)
deletes processed rows older than its retention window in **5 000-row
chunks**, each in its own short transaction, so autovacuum keeps up and
WAL doesn't spike on a busy table.

---

## Concurrency model

### Optimistic concurrency

`Sale.RowVersion` is a `bigint` column whose value is bumped by a
Postgres BEFORE UPDATE trigger:

```sql
CREATE OR REPLACE FUNCTION ambev_sales_bump_rowversion()
RETURNS trigger AS $$
BEGIN
  NEW."RowVersion" := COALESCE(OLD."RowVersion", 0) + 1;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;
```

EF Core treats the column as `ValueGeneratedOnAddOrUpdate +
IsConcurrencyToken`, so it issues `UPDATE … WHERE Id = @id AND
RowVersion = @oldVersion`, fetches the new value via `RETURNING`, and
throws `DbUpdateConcurrencyException` on a zero-row update.

> **Why a trigger and not `xmin`?** `xmin` is reset by VACUUM FREEZE
> when `age(xmin)` crosses `vacuum_freeze_table_age` (default 150M
> txns). A rare but catastrophic event that would invalidate every
> cached ETag at once. The trigger-managed bigint is monotonic across
> the row's lifetime and unaffected by FREEZE.

### HTTP ETag / If-Match

`GET /api/v1/sales/{id}` returns a strong ETag derived from
`RowVersion`. `POST` / `PUT` / `PATCH` also emit `ETag` so clients can
chain writes without a re-fetch. `PUT` and `DELETE` honour `If-Match`;
a stale value yields **412 Precondition Failed**. See the controller
helpers
[`SalesController.cs:181`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Sales/SalesController.cs#L181) and
[`SalesController.cs:188`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Sales/SalesController.cs#L188).

### Idempotency-Key

Stripe-style semantics, implemented in
[`IdempotencyMiddleware.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Middleware/IdempotencyMiddleware.cs):

- Only `2xx` responses are cached (24h TTL). Retrying with the same
  key after a 4xx re-runs the pipeline.
- The key is paired with a **SHA-256 of the canonical JSON body**
  (sorted keys, no whitespace) so `{"a":1}` and `{ "a": 1 }` replay
  each other but a different body returns 422.
- Concurrent requests with the same key race for an SET-NX-style lock;
  the loser gets 409 instead of running a parallel handler.
- Keys longer than 256 bytes are rejected (400) — bounded header
  surface.
- Backed by `IDistributedCache`: Redis when `ConnectionStrings:Redis`
  is set (multi-pod safe), in-memory fallback otherwise.

---

## Read path & 2nd-level cache

`ISaleReadCache` ([`Application/Sales/Common/`](../src/Ambev.DeveloperEvaluation.Application/Sales/Common/ISaleReadCache.cs))
sits in front of `GET /api/v1/sales/{id}`. Implementation:
[`DistributedSaleReadCache.cs`](../src/Ambev.DeveloperEvaluation.Application/Common/Caching/DistributedSaleReadCache.cs).

- Backed by the same `IDistributedCache` (Redis or in-memory fallback).
- 60-second absolute TTL as a safety net.
- **Explicitly evicted on every write path** (Update, Cancel,
  CancelItem, Delete) so the next read sees the new state immediately.
- All cache operations are **best-effort** — a Redis blip logs at
  warning and falls back to the DB. The cache is a latency
  optimisation, not a source of truth.

`ListSales` is **not** cached. The query patterns are too varied
(filters, ordering, page/cursor) to give a useful hit rate, and
invalidation would be more complex than the win. Documented as future
work — see [database.md](database.md#known-future-work).

---

## Pagination strategy

`GET /api/v1/sales` supports two mutually-exclusive modes:

| Mode | Trigger | Behaviour |
|---|---|---|
| Offset / page | `_page=N&_size=M` (default) | LIMIT/OFFSET + a separate `SELECT COUNT(*)` round trip. Page numbers, total pages, total count. |
| Keyset / cursor | `_cursor=<opaque>` | `WHERE (SaleDate, Id) < (cursorDate, cursorId)`, no COUNT. O(log n) per page; ordering is fixed to `SaleDate DESC, Id DESC` for stability. |

Mixing both returns 400. Cursors are opaque base64 strings — clients
should treat them as opaque, never parse. Source:
[`SaleCursor.cs`](../src/Ambev.DeveloperEvaluation.ORM/Repositories/SaleCursor.cs),
[`SaleRepository.cs`](../src/Ambev.DeveloperEvaluation.ORM/Repositories/SaleRepository.cs).

---

## Error contract

A single middleware (
[`ValidationExceptionMiddleware.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Middleware/ValidationExceptionMiddleware.cs)
) converts well-known exceptions to RFC 7807
`application/problem+json`. Specific types are matched before generic
ones; unhandled exceptions become 500 without leaking stack traces
(Serilog still records the full stack on the server side).

| Exception | HTTP | Title |
|---|---|---|
| `ValidationException` (FluentValidation) | 400 | Validation failed |
| `DomainException` | 400 | Domain rule violated |
| `ResourceNotFoundException` | 404 | Resource not found |
| `ConflictException` / Postgres 23505 / `DbUpdateConcurrencyException` | 409 | Conflict / Concurrent modification |
| `PreconditionFailedException` | 412 | Precondition failed |
| `UnauthorizedAccessException` | 401 | Unauthorized |
| Anything else | 500 | Internal server error (logged) |

---

## Cross-cutting concerns wiring

| Concern | Wired in | Library / mechanism |
|---|---|---|
| MediatR | `Program.cs:269` | `AddMediatR` over Application + WebApi assemblies |
| Validation pipeline | `Program.cs:282` | `IPipelineBehavior<,> → ValidationBehavior<,>` from Common |
| AutoMapper | `Program.cs:267` | `AddAutoMapper` over the same two assemblies |
| Logging | `Program.cs:37` + [`LoggingExtension.cs`](../src/Ambev.DeveloperEvaluation.Common/Logging/LoggingExtension.cs) | Serilog + `Enrich.FromLogContext` + TraceId/SpanId |
| OpenTelemetry | `Program.cs:229` | ASP.NET Core / HTTP client / EF Core instrumentation, OTLP exporter |
| Health checks | `Program.cs:173` + [`HealthChecksExtension.cs`](../src/Ambev.DeveloperEvaluation.Common/HealthChecks/HealthChecksExtension.cs) | `AddDbContextCheck<DefaultContext>` |
| Rate limiting | `Program.cs:123` | `Microsoft.AspNetCore.RateLimiting` fixed window |
| API versioning | `Program.cs:74` | `Asp.Versioning.Mvc` |
| Auth | `Program.cs:217` + [`AuthenticationExtension.cs`](../src/Ambev.DeveloperEvaluation.Common/Security/AuthenticationExtension.cs) | JwtBearer, HS256 ≥ 32 bytes |
| Forwarded headers | `Program.cs:293` | `ForwardedHeaders:KnownProxies/KnownNetworks` (CSV) |
| Composition | `Program.cs:265` + [`DependencyResolver.cs`](../src/Ambev.DeveloperEvaluation.IoC/DependencyResolver.cs) | Three `IModuleInitializer`s |

---

## Design decisions & rationale

### Why DDD-light and not Clean Architecture / vertical slices?

The template ships with a DDD-flavoured layered layout. The submission
keeps that boundary instead of refactoring to vertical slices because:

- The reviewer's mental model is the template's — divergence becomes
  noise.
- All Sales use cases follow the same shape (handler + validator +
  command + DTO + mapping). The layer-and-feature folder layout
  ([`Application/Sales/CreateSale/`](../src/Ambev.DeveloperEvaluation.Application/Sales/CreateSale/),
  etc.) already gives vertical-slice readability without breaking the
  template's contract.
- The aggregate is the architectural centre of gravity — keeping it
  pure (zero infra deps) is the win, regardless of folder shape.

### Why an outbox even though the challenge says "logging is fine"?

The cost is one table, one publisher, one background service, one
trigger. The win is that the event lifecycle is provably correct end-
to-end — a real-world property worth demonstrating in a senior
evaluation. Replacing `DeliverAsync` with a Kafka / RabbitMQ producer
is a single-class change.

### Why a Postgres trigger for `RowVersion` instead of `xmin` or app-managed?

- **`xmin`** is the path of least resistance but gets reset by VACUUM
  FREEZE — invalidates ETags catastrophically.
- **App-managed `RowVersion`** (handler increments) loses the
  concurrency guarantee against raw-SQL writers and would race itself
  under concurrent updates.
- **Trigger** is atomic with the row update, monotonic across the row's
  lifetime, immune to FREEZE, and survives raw-SQL writers.

### Why MediatR?

Decouples the controller from the handler so:

- The validation pipeline behaviour runs **once**, for any new use
  case, with no per-controller wiring.
- Unit tests for handlers do not need a controller, AutoMapper, or
  the HTTP stack — just `IRequestHandler<TIn, TOut>`.
- Cross-cutting concerns (caching, logging, transactions) can be added
  as additional `IPipelineBehavior<,>`s without touching handlers.

### Why pooled `DbContext`?

`AddDbContextPool` recycles the Model + change tracker across
requests. For a 100-rpm-per-pod API the latency win is small but the
allocation/GC reduction is meaningful at scale, and the model
compilation overhead is paid once at startup, not per request. The
pool size (256) is well above the realistic concurrency for one pod;
the bottleneck is the connection pool tuned via the connection string
(`Maximum Pool Size=200`).

---

## What's intentionally **not** here

- A real broker. Stand-in is the structured log; seam is
  `DeliverAsync`.
- HIBP / breach-list password screening. Roadmap.
- Refresh-token reuse detection (auto-revoke whole chain on replay).
  One-shot rotation + `jti` denylist are in place — see
  [security.md → JWT lifecycle](security.md#jwt-lifecycle); the
  detection step is a v1.1 enhancement.
- A separate Read DB / CQRS materialised view. The 2nd-level cache
  covers `GetById`; list-side reads are fast enough at expected scale
  given the partial indexes.

---

[← back to README](../README.md)
