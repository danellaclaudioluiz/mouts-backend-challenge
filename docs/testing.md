# Testing Strategy

Two suites, two intents:

- **Unit** — pure C# against the domain and the application handlers.
  Fast (sub-second), no external dependencies. Drives the discount
  policy, aggregate invariants, validators, and the
  repository/publisher interactions on the handler surface.
- **Integration** — end-to-end through
  `WebApplicationFactory<Program>` against a real Postgres
  testcontainer. Drives the HTTP surface, middleware (idempotency,
  exception → problem details), concurrency, outbox side-effects,
  rate limiting, health probes.

The split is deliberate: the unit suite stays green even if Docker is
down; the integration suite *requires* a working Docker daemon and is
where the real bug-finding happens for HTTP semantics.

[← back to README](../README.md)

---

## Running

```bash
cd template/backend

dotnet test                                                    # both suites
dotnet test tests/Ambev.DeveloperEvaluation.Unit               # unit only
dotnet test tests/Ambev.DeveloperEvaluation.Integration        # integration (needs docker)
```

Coverage HTML report (Coverlet + ReportGenerator, both auto-installed
by the script if missing):

```bash
./coverage-report.sh    # macOS / Linux
coverage-report.bat     # Windows
# open template/backend/TestResults/CoverageReport/index.html
```

CI runs both suites on `ubuntu-latest` plus a Gitleaks scan — see
[devops.md → CI](devops.md#continuous-integration).

---

## Unit suite

Project:
[tests/Ambev.DeveloperEvaluation.Unit/](../tests/Ambev.DeveloperEvaluation.Unit/).

Frameworks: xUnit, FluentAssertions, NSubstitute, Bogus (test data).
No external dependencies, no DB, no host.

### Coverage matrix

| Area | File | What it asserts |
|---|---|---|
| Discount policy tiers | [`Domain/Services/SaleItemDiscountPolicyTests.cs`](../tests/Ambev.DeveloperEvaluation.Unit/Domain/Services/SaleItemDiscountPolicyTests.cs) | 0% / 10% / 20% tiers; > 20 throws; ≤ 0 qty/price throws; tier borders × awkward unit prices (0.01, 33.33, 999.99) match the rounding contract (AwayFromZero, 2 dp) |
| Sale aggregate invariants | [`Domain/Entities/SaleTests.cs`](../tests/Ambev.DeveloperEvaluation.Unit/Domain/Entities/SaleTests.cs) | `AddItem` rejects duplicate productId, `AddItem` on cancelled sale throws, `Cancel` is idempotent + emits exactly one event, `CancelItem` recalculates total, unknown id throws, cancel-cancelled-item is a no-op, `Cancel` cascades to all active items |
| Sale items | [`Domain/Entities/SaleItemTests.cs`](../tests/Ambev.DeveloperEvaluation.Unit/Domain/Entities/SaleItemTests.cs) | Line total = `qty × price − discount` across every tier |
| Validators | [`Application/.../{UseCase}ValidatorTests.cs`](../tests/Ambev.DeveloperEvaluation.Unit/Application/) | Required fields, length caps, date bounds, items-array cap, per-item bounds |
| Handlers | [`Application/.../{UseCase}HandlerTests.cs`](../tests/Ambev.DeveloperEvaluation.Unit/Application/) | Repository + event publisher called with the right shape; duplicate `SaleNumber` raises `ConflictException`; missing aggregate raises `ResourceNotFoundException`; cache evictions happen on the write paths |

Mocks are `NSubstitute.For<>`-style stand-ins behind the domain
contracts (`ISaleRepository`, `IDomainEventPublisher`, `ISaleReadCache`).
Handlers under test never see EF Core.

---

## Integration suite

Project:
[tests/Ambev.DeveloperEvaluation.Integration/](../tests/Ambev.DeveloperEvaluation.Integration/).

### Fixture

[`SalesApiFactory.cs`](../tests/Ambev.DeveloperEvaluation.Integration/SalesApiFactory.cs)
is a `WebApplicationFactory<Program> + IAsyncLifetime`:

1. Boots a `postgres:16` testcontainer.
2. Sets `ConnectionStrings__DefaultConnection` and `Jwt__SecretKey` as
   environment variables **before** the host is first built (the
   builder reads env vars at construction time, before the in-memory
   collection lands).
3. Runs `Database.MigrateAsync()` once.
4. Exposes `ResetDatabaseAsync()` — `TRUNCATE … RESTART IDENTITY
   CASCADE` on `SaleItems`, `OutboxMessages`, `Sales`, `Users`.
5. Mints an `Admin` bearer token in `ConfigureClient` so every test
   client is **authenticated by default** — `CreateAnonymousClient()`
   for the boundary tests.
6. Bumps `RateLimit:PermitLimit` to 10 000 so the broad suite doesn't
   trip the throttle. The dedicated rate-limit test class uses
   [`RateLimitedSalesApiFactory.cs`](../tests/Ambev.DeveloperEvaluation.Integration/RateLimitedSalesApiFactory.cs)
   with `PermitLimit = 5, WindowSeconds = 2`.

### Isolation

All integration tests live in the
[`IntegrationCollection`](../tests/Ambev.DeveloperEvaluation.Integration/IntegrationCollection.cs).
xUnit serialises tests in the same collection, so per-test
`TRUNCATE … CASCADE` resets are race-free without locking.

The testcontainer boots **once per suite** (not per class), which keeps
the wall-clock cost reasonable for ~50 cases.

### Helpers

| Helper | Purpose |
|---|---|
| [`OutboxAsserter.cs`](../tests/Ambev.DeveloperEvaluation.Integration/Helpers/OutboxAsserter.cs) | Reads `OutboxMessages` directly so tests can assert "the side-effect was persisted in the same transaction" without waiting on the dispatcher's polling clock. Deserialises the CloudEvents envelope's `data` field and asserts the `eventId` presence (consumer dedup contract). |
| [`PayloadBuilder.cs`](../tests/Ambev.DeveloperEvaluation.Integration/Helpers/PayloadBuilder.cs) | Fluent builder for `CreateSaleRequest` / `UpdateSaleRequest` test bodies. |
| [`ProblemDetailsAsserter.cs`](../tests/Ambev.DeveloperEvaluation.Integration/Helpers/ProblemDetailsAsserter.cs) | Asserts that 4xx / 5xx responses are `application/problem+json` with the expected `status`, `title`, and `instance`. |

### Coverage matrix

| Area | File | What it asserts |
|---|---|---|
| Happy paths + ETag/Location | [`SalesEndpointsTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/SalesEndpointsTests.cs) | POST returns 201 + `Location` + `ETag`; GET returns 404 problem details; validation errors return 400 problem details; PATCH `/cancel` toggles `IsCancelled` + writes a `sale.cancelled.v1` outbox row; stale `If-Match` returns 412 |
| Idempotency-Key | `SalesEndpointsTests.cs` | Replay returns cached 201 byte-equal to the original; different body returns 422; whitespace + key-order variants share the canonical hash; 4xx responses are not cached; key > 256 chars returns 400 |
| Concurrency races | [`ConcurrencyTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/ConcurrencyTests.cs) | Two PUTs with the same stale `If-Match` → exactly one 200 + one 412/409 and the DB reflects the winner only; 5 concurrent POSTs with the same `Idempotency-Key` → exactly one `Sales` row regardless of in-flight lock outcome |
| If-Match precondition | [`IfMatchEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/IfMatchEndpointTests.cs) | `If-Match: *` opt-out; missing header proceeds; current ETag succeeds; stale value returns 412 |
| Update | [`UpdateSaleEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/UpdateSaleEndpointTests.cs) | Happy path + diff-style update keeps stable item ids; update against a cancelled sale returns 400; unknown id returns 404 |
| Delete | [`DeleteSaleEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/DeleteSaleEndpointTests.cs) | Hard-delete cascades items; current `If-Match` succeeds; stale `If-Match` returns 412; unknown id returns 404 |
| Cancel item | [`CancelSaleItemEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/CancelSaleItemEndpointTests.cs) | Item flagged cancelled + total recalculated; second cancel on same item is idempotent (no extra event); unknown sale → 404; unknown item → 400 |
| List + pagination + filters | [`ListSalesEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/ListSalesEndpointTests.cs) | Page/size paging; customer/branch/`isCancelled` filters; ordering; bad order key → 400; oversize page → 400; empty page is well-formed; keyset cursor mode; `_page` + `_cursor` together → 400 |
| Boundaries | [`BoundaryEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/BoundaryEndpointTests.cs) | Exactly `MaxItemsPerSale` (100) items accepted; 101 items → 400; duplicate `productId` across lines → 400 (cap cannot be split) |
| Authorization | [`AuthorizationEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/AuthorizationEndpointTests.cs) | Anonymous → 401 on authenticated endpoints; `POST /api/v1/auth` / `POST /api/v1/users` / `/health/*` reachable anonymous; mass-assignment defence — `role: "Admin"` in signup body is silently dropped |
| Rate limit | [`RateLimitEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/RateLimitEndpointTests.cs) | Dedicated factory with `PermitLimit=5, WindowSeconds=2`: bursts beyond the permit return 429; window reset releases the next burst |
| Abuse vectors | [`AbuseEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/AbuseEndpointTests.cs) | Oversize payloads, malformed JSON, header injection attempts surface as 400 problem details — never 500 |
| Health | [`HealthEndpointTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/HealthEndpointTests.cs) | `/health/live` returns Healthy; `/health/ready` includes the Postgres DB probe; `/health` returns the full report; stopping the testcontainer drops `/health/ready` to 503 |
| Outbox lifecycle | [`OutboxLifecycleTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/OutboxLifecycleTests.cs) | The dispatcher claims, publishes, marks rows processed; dead-letter cap behaviour; LISTEN/NOTIFY wakeup |
| Missing scenarios | [`MissingScenarioTests.cs`](../tests/Ambev.DeveloperEvaluation.Integration/MissingScenarioTests.cs) | Pinned `[Fact(Skip = "…")]` entries that document expected gaps so reviewers see them without diffing the suite |

### Authenticated by default

`SalesApiFactory.ConfigureClient` adds a `Bearer <admin-jwt>` header
to every client returned by `CreateClient()`. Tests that exercise the
auth boundary opt out via `CreateAnonymousClient()`:

```csharp
var anonymous = _factory.CreateAnonymousClient();
var response = await anonymous.PostAsJsonAsync("/api/v1/sales", body);
response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
```

The motivation is to prevent the suite from accidentally bypassing
authentication — a previous iteration of the factory created clients
without a token, and the entire happy path silently exercised the
`[AllowAnonymous]` path (which had been removed). The default-auth
posture surfaces that class of regression instantly.

---

## What is NOT tested (intentionally)

- **A real broker.** The dispatcher's `DeliverAsync` writes to the
  structured log. Tests assert outbox rows reach `ProcessedAt`, not
  that they materialise on Kafka / RabbitMQ — out of scope.
- **JWT issuance against an external IdP.** Token signing is
  validated in-process (`MintBearerToken` uses the registered
  `IJwtTokenGenerator`).
- **PUT replacing the whole items array.** Documented gap — see
  [api.md → known limitation](api.md#put-known-limitation). Pinned as a
  `[Fact(Skip = "…")]` so the gap is visible in the test output.
- **Distributed-cache failure injection.** The cache implementation is
  best-effort (logs + falls back to DB on failure). Code path is
  exercised via unit tests against an in-memory `IDistributedCache`.

---

## CI

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs three
jobs on every push and pull request:

| Job | What it does |
|---|---|
| `build-test` | `dotnet restore`, `dotnet build -c Release`, `dotnet format --verify-no-changes`, unit suite with Coverlet → Cobertura → Codecov |
| `integration-test` | Builds and runs the Testcontainers Postgres suite on `ubuntu-latest` (Docker is pre-installed on the runner) |
| `secret-scan` | `gitleaks/gitleaks-action` on full history with `fetch-depth: 0` |

Failure in any job blocks the PR. `dotnet format --verify-no-changes`
enforces the `.editorconfig`, so formatting drift is caught at PR
time, not in code review.

---

[← back to README](../README.md)
