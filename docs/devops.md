# DevOps & Infrastructure

How the service is built, packaged, configured, run, observed, and
shipped. Bias toward "boring is good": multi-stage Docker, env-var
configuration, fail-fast on missing secrets, native ASP.NET health
probes, OTLP-compatible telemetry.

[← back to README](../README.md)

---

## Local development

The `.env.example` file is the canonical list of
required secrets. Copy it once:

```bash
cp .env.example .env       # gitignored
# fill in real values; generate strong ones with:
#   openssl rand -base64 24    # passwords
#   openssl rand -base64 32    # JWT_SECRET_KEY (must be ≥ 32 bytes)
```

### Path A — everything in Docker

```bash
docker compose up -d ambev.developerevaluation.database \
                    ambev.developerevaluation.cache
docker compose up --build ambev.developerevaluation.webapi
```

Swagger UI: <https://localhost:8081/swagger> (port published by the
WebApi service when Visual Studio's `DockerCompose` profile is used,
or by the `docker-compose.override.yml` published-port section locally).

### Path B — host-running .NET, containers for dependencies

```bash
docker compose up -d ambev.developerevaluation.database \
                    ambev.developerevaluation.cache

# The override file in this repo publishes the DB / Redis / Mongo ports
# to 127.0.0.1 so the host process can reach them; the base compose
# deliberately keeps them private.

dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=localhost;Port=5432;Database=developer_evaluation;Username=developer;Password=..." \
  --project src/Ambev.DeveloperEvaluation.WebApi

dotnet user-secrets set "Jwt:SecretKey" "<32+ bytes>" \
  --project src/Ambev.DeveloperEvaluation.WebApi

dotnet ef database update \
  --project src/Ambev.DeveloperEvaluation.ORM \
  --startup-project src/Ambev.DeveloperEvaluation.WebApi

dotnet run --project src/Ambev.DeveloperEvaluation.WebApi
```

---

## Docker

### Image build

[`src/Ambev.DeveloperEvaluation.WebApi/Dockerfile`](../src/Ambev.DeveloperEvaluation.WebApi/Dockerfile)
is a standard ASP.NET multi-stage build, three stages:

| Stage | Base | Purpose |
|---|---|---|
| `base` | `mcr.microsoft.com/dotnet/aspnet:8.0` | Runtime layer; `USER app` (non-root); exposes 8080 + 8081. |
| `build` | `mcr.microsoft.com/dotnet/sdk:8.0` | Restore + Release build. |
| `publish` → `final` | `base` | `dotnet publish -c Release` artefacts copied onto the runtime layer. |

`.dockerignore` keeps `bin/`, `obj/`, secrets, and the `tests/` folder
out of the build context — a `docker build` is fast and reproducible.

The container runs as the `app` user out of the base image; no
unnecessary capabilities are added. Read-only root FS / `cap_drop:
ALL` is a deployment-time hardening not enforced by the compose file
(documented as future work).

### Compose topology

[`docker-compose.yml`](../docker-compose.yml) declares
four services:

| Service | Image | Notes |
|---|---|---|
| `ambev.developerevaluation.webapi` | built from the Dockerfile above | `ASPNETCORE_HTTPS_PORTS=8081`, `ASPNETCORE_HTTP_PORTS=8080`. Mounts host user-secrets and ASP.NET dev cert read-only. |
| `ambev.developerevaluation.database` | `postgres:16` | **No host port published by default** — only the WebApi container reaches it over the compose network. |
| `ambev.developerevaluation.cache` | `redis:7.4.1-alpine` | `requirepass` enforced; no host port published in the base file. |
| `ambev.developerevaluation.nosql` | `mongo:8.0` | Optional, kept from the template; not used by the Sales feature. |

`docker compose up` will **refuse to start** if any of these env vars
are unset (the `${VAR:?message}` syntax in compose):

`POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`,
`JWT_SECRET_KEY`, `MONGO_USER`, `MONGO_PASSWORD`, `REDIS_PASSWORD`.

Misconfiguration is loud — never a silent default that ships an empty
or "changeme" credential.

### Override file

[`docker-compose.override.yml`](../docker-compose.override.yml)
is **local-development only**. Docker auto-merges it on top of the
base compose, publishing Postgres / Redis / Mongo to `127.0.0.1` so
host-process devs can reach the dependencies via `localhost:5432`,
`localhost:6379`, `localhost:27017`.

In a deployed environment the override file is absent and the DB
remains private to the compose network — only the WebApi container
can talk to it.

---

## Configuration

Twelve-Factor app: config comes from the environment, secrets are
externalised.

### Required configuration keys

All read via `IConfiguration`, which means each can be set through
**any** of: `appsettings.json`, `appsettings.{Environment}.json`,
`dotnet user-secrets` (dev only), or an environment variable using the
ASP.NET double-underscore convention
(`Section__SubSection__Key`).

| Key | Mandatory | Notes |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | always | Empty/missing → startup fails fast ([Program.cs:187](../src/Ambev.DeveloperEvaluation.WebApi/Program.cs#L187)). |
| `Jwt:SecretKey` | always | Must be ≥ 32 bytes. HS256. Re-issuing this invalidates every outstanding token. |
| `Jwt:Issuer` | outside Development | Set to this API's URL. A leaked key cannot mint tokens accepted by another service. |
| `Jwt:Audience` | outside Development | Resource the tokens grant access to. |
| `ConnectionStrings:Redis` | recommended in multi-pod | When set, Idempotency-Key cache + 2nd-level sale cache use Redis instead of in-memory. |
| `Swagger:Enabled` | optional | Defaults to `true` only in Development; **always** set explicitly in production (`false`). |
| `Cors:AllowedOrigins` | always | Array of allowed origins; empty array denies all cross-origin traffic. |
| `ForwardedHeaders:KnownProxies` | when behind a proxy | CSV of trusted proxy IPs. Without an allow-list `X-Forwarded-For` is **rejected** outside loopback (Development/Test only). |
| `ForwardedHeaders:KnownNetworks` | when behind a proxy | CSV of trusted networks (`10.0.0.0/8`, etc.). |
| `RateLimit:PermitLimit` / `RateLimit:WindowSeconds` | optional | Defaults 100 / 60. |
| `RateLimit:AuthPermitLimit` / `RateLimit:AuthWindowSeconds` | optional | Defaults 5 / 60. |
| `OpenTelemetry:OtlpEndpoint` (or `OTEL_EXPORTER_OTLP_ENDPOINT`) | optional | OTLP exporter address; traces + metrics export silently no-op when unset. |
| `AllowedHosts` | recommended | Defaults to `*` in `appsettings.json` — override in production with a CSV of hostnames to reject Host-header injection. |

### Configuration sources, precedence (low → high)

1. `appsettings.json` — ships empty secrets; **the app refuses to
   start with empty `DefaultConnection` or `Jwt:SecretKey`**.
2. `appsettings.{Environment}.json`.
3. `dotnet user-secrets` (Development only).
4. Environment variables.

### Dev-only ergonomics

- `appsettings.Development.json` lists CORS dev origins
  (`http://localhost:5173`, `http://localhost:4200`).
- `.env` (gitignored) feeds `docker compose`.
- `.env.example` (committed) is the schema — copy-rename-fill.

---

## Health checks

Three endpoints, all anonymous. The Postgres readiness check gates
load-balancer traffic in production.

| Route | Probe | Status codes |
|---|---|---|
| `/health/live` | `liveness`-tagged checks only (process up) | 200 healthy, 503 unhealthy |
| `/health/ready` | `readiness`-tagged checks (Postgres via `AddDbContextCheck`) | 200 healthy, 503 when DB unreachable |
| `/health` | catch-all (full report) | 200 / 200 (degraded) / 503 |

`BackgroundService` exceptions are configured to be **ignored**
(`HostOptions.BackgroundServiceExceptionBehavior = Ignore`) so a DB
outage observed by the outbox dispatcher cannot tear down the API
host. The dispatcher's own catch-and-log loop reports the outage; the
DB outage surfaces independently through `/health/ready` so the load
balancer can drain the pod.

Outside Development / Test, the readiness response **strips**
exception messages — leaking connection strings or DNS hostnames to
anonymous callers is unnecessary. Operators see the real detail in
the server log.

Source:
[`HealthChecksExtension.cs`](../src/Ambev.DeveloperEvaluation.Common/HealthChecks/HealthChecksExtension.cs).

---

## Observability

### Structured logging

Serilog, configured in
[`LoggingExtension.cs`](../src/Ambev.DeveloperEvaluation.Common/Logging/LoggingExtension.cs):

- `.Enrich.FromLogContext()` + `.Enrich.WithSpan()` so every line
  carries `TraceId` / `SpanId` pulled from the ambient OpenTelemetry
  `Activity`. Log lines join their traces in Tempo / Jaeger / Datadog
  without manual correlation ids.
- `.Enrich.WithMachineName()`, `Environment`, `Application` properties.
- Sinks: console (always) + rolling file `logs/log-.txt` (Release
  only) at daily rolls, 50 MB per file, 14-day retention. Production
  should ship to a real sink (Loki / Splunk / Datadog) — the local
  file is a fallback.
- Filter: 200-OK `/health` probe lines are dropped. Every other
  Information / Warning / Error / Fatal stays.

### OpenTelemetry

[Program.cs:229](../src/Ambev.DeveloperEvaluation.WebApi/Program.cs#L229)

Traces and metrics, both registered with OTLP exporters when
`OpenTelemetry:OtlpEndpoint` (or the OTEL standard env var
`OTEL_EXPORTER_OTLP_ENDPOINT`) is set:

- **Traces**: ASP.NET Core, outbound HttpClient, EF Core (SQL).
- **Metrics**: ASP.NET Core, HttpClient, .NET runtime (GC / threadpool
  / connections).
- Service resource: `serviceName: ambev.developerevaluation.webapi`,
  `serviceVersion: <assembly version>`.

Spans / metrics still flow to in-process listeners when no OTLP
endpoint is configured — local devs can plug a console exporter
without touching code.

---

## Continuous integration

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs **six
jobs**. The four PR-blocking ones fire on every push and pull request
to `main`; the two heavy ones (`mutation-testing`, `supply-chain`) run
nightly + on `workflow_dispatch` so they don't slow the PR loop.

| Job | Trigger | Steps | Purpose |
|---|---|---|---|
| `build-test` | push, PR | restore → build Release → `dotnet format --verify-no-changes` → unit tests with Coverlet `XPlat Code Coverage` → upload TRX + coverage artefacts → Codecov | Build + format gate + fast unit suite + coverage publication |
| `integration-test` | push, PR | restore → build Release → integration test project | Testcontainers Postgres on the ubuntu-latest runner's bundled Docker socket |
| `migration-validate` | push, PR | install `dotnet-ef` → `dotnet ef migrations script --idempotent --output migrations.sql` → assert non-empty → upload script artifact | Catches a migration that references a model that no longer compiles BEFORE it lands in a deploy |
| `supply-chain` | nightly + manual | `dotnet list package --vulnerable --include-transitive` (fails on `Critical`) → install `CycloneDX` → emit SBOM artifact | Supply-chain visibility (provenance + vulnerability gate) without slowing PRs |
| `mutation-testing` | nightly + manual | install `dotnet-stryker` → `dotnet stryker --config-file stryker-config.json` → upload HTML report | Validates the test suite actually catches mutations (Domain + Application; thresholds high 85 / low 70 / break 60 — see [`stryker-config.json`](../stryker-config.json)) |
| `secret-scan` | push, PR | `gitleaks/gitleaks-action@v2` over full history (`fetch-depth: 0`) | Catches leaked credentials across the entire git history, not just the diff |

Failure in any PR-blocking job blocks the PR. `dotnet format
--verify-no-changes` enforces `.editorconfig`, so formatting drift is
caught at PR time rather than in code review.

[`.github/dependabot.yml`](../.github/dependabot.yml) layers on top:
weekly NuGet bumps (grouped by Microsoft runtime / OpenTelemetry / test
tooling), GitHub Actions, and the WebApi Dockerfile. Max 10 open PRs to
keep the queue tractable.

`permissions: contents: read` is set globally — the workflow has no
write access to the repo, defence in depth against a compromised
action.

---

## Production deployment

This submission is a code drop — it ships no `terraform/` or
`helm/` directory. The runtime expectations are concrete enough that
a reviewer can deduce a deployable shape:

| Concern | What's expected |
|---|---|
| Compute | Container (Dockerfile is the contract). Stateless — scale horizontally. |
| Database | Postgres 16 with `pgcrypto` and `pg_trgm` extensions available. |
| Cache | Redis 7.x for multi-pod safety. Single-pod can fall back to in-memory. |
| Migrations | Apply via `dotnet ef database update` at deploy time. **Not** auto-run at startup. |
| Secrets | Inject `Jwt__SecretKey` and `ConnectionStrings__DefaultConnection` (and `__Redis` when applicable) via env vars from your secret manager. |
| TLS | Terminate at the proxy; populate `ForwardedHeaders:KnownProxies/KnownNetworks` so the API trusts `X-Forwarded-*` only from your proxy IPs. |
| Health probe | `/health/live` for liveness, `/health/ready` for readiness. |
| Logging | Serilog → STDOUT → your log aggregator. |
| Traces | OTLP exporter to your collector via `OTEL_EXPORTER_OTLP_ENDPOINT`. |
| Rate limit | The in-process limiter is per-pod. For fleet-wide limits, fronting with an API gateway is the natural extension. |
| CORS | Set `Cors:AllowedOrigins` to your front-end origin(s). An empty list denies all cross-origin traffic — deliberate. |

### Multi-pod / rolling-deploy gotcha

The application does **not** call `Database.MigrateAsync()` at startup
— migrations are applied separately. If a future iteration runs
migrations at startup, wrap the call in a Postgres advisory lock — see
[database.md → Auto-migration](database.md#auto-migration--multi-pod-rollouts).

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Startup throws `ConnectionStrings:DefaultConnection is not configured` | The empty placeholder in `appsettings.json` was not overridden. Set via env var, user-secrets, or `.env`. |
| Startup throws `Jwt:SecretKey must be at least 32 bytes` | Generate one with `openssl rand -base64 32`. |
| Startup throws `Jwt:Issuer is required outside Development` | Outside Dev / Test the issuer + audience are mandatory. Set `Jwt__Issuer` and `Jwt__Audience`. |
| `/health/ready` returns 503 | Postgres is unreachable. Check the connection string, the container is up, the host firewall, the pool exhaustion (`Maximum Pool Size`). |
| 412 Precondition Failed on PUT/DELETE | Stale `If-Match`. Re-fetch the resource to get a fresh `ETag` and retry. |
| 422 on POST with `Idempotency-Key` | Same key was first used with a different body. Generate a fresh key for the new request. |
| 429 Too Many Requests | Rate limit. Default is 100 req/min/principal; the `auth-strict` policy is 5/min/IP. Tune via `RateLimit:*` config. |
| Integration tests hang on first run | Docker is starting / pulling the `postgres:16` image. Subsequent runs reuse the cache. |
| `docker compose up` errors with `... is required` | Compose-level fail-fast on a missing env var. Copy `.env.example` to `.env` and fill in values. |
| Swagger 404 in production | `Swagger:Enabled` defaults to `false` outside Development. Enable explicitly if intended — and never in a public-facing prod. |
| Outbox rows pile up at high `Attempts` | Look at `LastError` per row. Dead-letters at 10 — investigate the publish failure or backfill manually after a fix. |

---

## Useful commands

```bash
# Format the whole solution
dotnet format Ambev.DeveloperEvaluation.sln

# Add a new EF Core migration
dotnet ef migrations add <Name> \
  --project src/Ambev.DeveloperEvaluation.ORM \
  --startup-project src/Ambev.DeveloperEvaluation.WebApi

# Inspect outbox state
docker compose exec ambev.developerevaluation.database psql -U $POSTGRES_USER -d $POSTGRES_DB \
  -c 'SELECT "EventType", count(*) FILTER (WHERE "ProcessedAt" IS NULL) AS pending,
              count(*) FILTER (WHERE "ProcessedAt" IS NOT NULL) AS processed,
              max("Attempts") AS max_attempts
      FROM "OutboxMessages" GROUP BY 1;'

# Dump the current schema
docker compose exec ambev.developerevaluation.database pg_dump \
  -U $POSTGRES_USER -d $POSTGRES_DB --schema-only

# Cancel a sale via curl (assuming local + bearer token in $TOKEN)
curl -sk -X PATCH -H "Authorization: Bearer $TOKEN" \
  https://localhost:8081/api/v1/sales/<id>/cancel | jq .
```

---

[← back to README](../README.md)
