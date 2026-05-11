# Security

What's defended, what's deferred, and where every control lives in the
code. Posture reflects the second audit pass on this submission: 87/100,
production-ready with two operational conditions, full record kept in
the project log.

[← back to README](../README.md)

---

## Threat model in one paragraph

A JSON-only REST API over JWT, fronted by a reverse proxy in
production. The hostile inputs are HTTP request bodies, query strings,
headers, and bearer tokens. The hostile observers are anyone who can
inspect logs, the cache, or the DB. The non-goals are protecting
against a compromised database admin, a compromised host, or a
malicious operator with secret-store access — those belong to a
broader infrastructure threat model.

---

## Authentication

JWT Bearer, **mandatory by default**:

```csharp
// Program.cs:257
options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .Build();
```

Every endpoint is authenticated unless it explicitly opts out via
`[AllowAnonymous]`. The four anonymous endpoints are:

- `POST /api/v1/auth` — login. Has nowhere to put a token yet.
- `POST /api/v1/users` — self-service signup.
- `/health/live`, `/health/ready`, `/health` — load-balancer probes.

> The previous template default — controllers without `[Authorize]`
> were **public** — meant a single forgotten attribute would ship a
> whole feature unauthenticated. The fallback policy inverts that
> bias.

### JWT signing posture

[`AuthenticationExtension.cs`](../src/Ambev.DeveloperEvaluation.Common/Security/AuthenticationExtension.cs)

- **HS256**.
- Signing key (`Jwt:SecretKey`) must be **≥ 32 bytes**. The startup
  hard-fails with a clear message otherwise — HS256 keys shorter than
  the digest size are trivially brute-forceable.
- `Jwt:Issuer` and `Jwt:Audience` are **mandatory outside Development
  and Test**. Startup hard-fails if either is missing — a leaked key
  in one service cannot mint tokens accepted by another tenant /
  audience.
- `ClockSkew = TimeSpan.Zero` — no implicit grace period on token
  expiration.
- `RequireHttpsMetadata = !IsDevelopment()` — JWKS metadata is fetched
  over HTTPS only.

### Login flow defences

[`AuthenticateUserHandler.cs`](../src/Ambev.DeveloperEvaluation.Application/Auth/AuthenticateUser/AuthenticateUserHandler.cs)

- **Single response message** across "no such user" / "wrong password"
  / "inactive user" — `Invalid credentials` for all three. No
  separate timing or status code per branch.
- **Constant-time path on unknown email**. `BCryptPasswordHasher`
  pre-computes a frozen dummy hash at the same work factor (12) at
  process startup, and the handler always calls `VerifyPassword`,
  passing the dummy hash if the user lookup miss. The login takes
  the same ~hundreds-of-ms BCrypt cost whether the email is real or
  not — no enumeration through response timing.
- Inactive users (`Status != Active`) are also rejected with the
  same opaque message, logged server-side.

### Password hashing

[`BCryptPasswordHasher.cs`](../src/Ambev.DeveloperEvaluation.Common/Security/BCryptPasswordHasher.cs)

- `BCrypt.Net.BCrypt` at work factor **12** — OWASP 2024 guidance for
  high-sensitivity / regulated APIs. Each +1 doubles cost.
- No custom crypto; no plain SHA / no MD5; no rolling-your-own salt.

### JWT trade-offs

The JWT lifetime is **8 hours** with **no refresh, no `jti` claim, no
denylist**.

| Position | Why this was accepted |
|---|---|
| Refresh tokens — deferred | Adds a stateful side (refresh-token store, rotation, reuse detection) without buying much for an internal-facing API. Roadmap v1.1. |
| `jti` / denylist — deferred | The denylist needs the same Redis or DB write path on every authn'd request. Caps p99 latency. Trade-off was made consciously. |
| Lifetime ≤ 8h | Bounds the blast radius of a stolen token. |
| Rotate by re-issuing `JWT_SECRET_KEY` | Every outstanding token becomes invalid immediately. |

**Operational runbook entry**: if a token leaks, rotate
`JWT__SECRET_KEY` and redeploy — the entire fleet invalidates within
seconds (next signature check fails).

---

## Authorization

Out of the box this submission is **authenticated-not-authorized**:
every authenticated user can call every Sales endpoint. The
`User.Role` enum (`Customer`, `Manager`, `Admin`) is persisted and
returned in the token; layering role-based policy on the controller is
a one-attribute change per endpoint.

### Mass-assignment defence

`CreateUserRequest` deliberately **does not expose `Role` or `Status`**
([`CreateUserRequest.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Features/Users/CreateUser/CreateUserRequest.cs)).
The handler hard-codes `role=Customer + status=Active` regardless of
the body. A request that ships `{ "role": "Admin" }` is silently
dropped at the JSON binding step, not "filtered" by hand. The
integration suite asserts this exact scenario.

---

## Transport hardening

[Program.cs:349](../src/Ambev.DeveloperEvaluation.WebApi/Program.cs#L349)

- **HSTS** outside Development / Test (1 year, includeSubDomains).
  Browsers refuse plain-HTTP downgrades for the next year, including
  on subdomains.
- **HTTPS redirect** for non-HTTPS traffic.
- **Forwarded headers** (`X-Forwarded-For`, `X-Forwarded-Proto`) are
  honoured **only** from configured `ForwardedHeaders:KnownProxies` /
  `KnownNetworks`. Without an allow-list, an attacker can spoof their
  IP through any reachable header — poisoning logs, the rate-limit
  partition key, and any IP-based ACLs downstream. Loopback is the
  one auto-trusted range in Dev / Test.

### Security headers

[Program.cs:357](../src/Ambev.DeveloperEvaluation.WebApi/Program.cs#L357)

Cheap defence in depth, even for a JSON API:

| Header | Value | Rationale |
|---|---|---|
| `X-Content-Type-Options` | `nosniff` | Forbids MIME-type sniffing. |
| `X-Frame-Options` | `DENY` | Prevents framing — irrelevant for JSON, free to set. |
| `Referrer-Policy` | `no-referrer` | No cross-origin Referer leakage. |
| `Content-Security-Policy` | `default-src 'none'; frame-ancestors 'none'` | The API never serves HTML; a tight CSP signals intent and blocks anything that does. |

### JSON encoding

`JsonOptions.Encoder = JavaScriptEncoder.Default` instead of the
permissive default. A stored `CustomerName` like `<script>` round-trips
on the wire as `<script>` — JSON-only consumers are fine
either way, but the strict encoder removes a class of HTML-injection
surprise for clients that pipe responses straight into `innerHTML`.

### CORS

[Program.cs:90](../src/Ambev.DeveloperEvaluation.WebApi/Program.cs#L90)

Restrictive by default. `Cors:AllowedOrigins` is the only knob —
empty list → **deny all cross-origin traffic** (preflight fails
loudly rather than silently allowing everything). The previous
`AllowAnyOrigin()` shortcut combined with a leaked-token scenario
was equivalent to a full CORS bypass; the new posture refuses to
ship that surface.

---

## Rate limiting

Two policies via `Microsoft.AspNetCore.RateLimiting`:

| Policy | Partition | Default | Override keys |
|---|---|---|---|
| `api` (default on all controllers) | authenticated user id (`ClaimTypes.NameIdentifier`), falls back to remote IP | 100 req / 60 s | `RateLimit:PermitLimit`, `RateLimit:WindowSeconds` |
| `auth-strict` (on `POST /api/v1/auth`) | remote IP | 5 req / 60 s | `RateLimit:AuthPermitLimit`, `RateLimit:AuthWindowSeconds` |

- **Why per-principal for `api`?** Corporate clients sharing a NAT IP
  would otherwise throttle each other under a shared partition.
- **Why per-IP for `auth-strict`?** The caller has no principal yet —
  IP is the only stable axis. 5 req/min/IP caps an attacker at
  ~300 password attempts/hour against any one username, which the
  BCrypt work factor 12 + opaque error message renders impractical.

Over-limit responses are bare `429 Too Many Requests` (no body) — the
limiter rejects before the controller runs, so there's no leakage.

The limiter is **per-pod**. Fleet-wide limits are an API-gateway
concern; documented as a deployment-level extension.

---

## Idempotency-Key

[`IdempotencyMiddleware.cs`](../src/Ambev.DeveloperEvaluation.WebApi/Middleware/IdempotencyMiddleware.cs)

Security-relevant properties beyond the safety story documented in
[architecture.md](architecture.md#idempotency-key):

- Header length is bounded at 256 bytes — caps an attacker's ability
  to flood the cache with mega-keys.
- Body fingerprint is SHA-256 of the **canonical** JSON form (sorted
  keys, no whitespace) — reuse with a tampered body returns 422, not
  a spurious 201 from a different request happening to share a key.
- Cache TTL is 24 h; in-flight lock TTL is 30 s. Both are bounded so a
  pathological key cannot pin cache pages indefinitely.

---

## Secrets management

- **No secrets in git history**. `.env` is gitignored; `.env.example`
  is the schema. Compose refuses to start with any required variable
  unset (`${VAR:?message}`).
- **No secrets in `appsettings.json`** — placeholders are empty
  strings and the app hard-fails at startup if they remain empty.
- `dotnet user-secrets` for host-running development. Stored outside
  the working tree.
- Production: inject via env vars from your secret manager (Vault /
  AWS Secrets Manager / Azure Key Vault / `--env-file` from a
  CI-managed file).
- `gitleaks` runs on every push / PR over the full history
  (`fetch-depth: 0`) — see
  [`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

If a secret reaches the remote git history, **rotate** the secret and
**purge** the history (`git filter-repo --replace-text`). The
early-development Postgres password that originally shipped with the
template was scrubbed via `git filter-repo` and the rewritten history
force-pushed; running `git log --all -S '<the-old-string>'` should now
return nothing. The full procedure is documented in
[`runbook.md` § 3](runbook.md#3-jwt-secret-leaked--rotated).

---

## Information disclosure

- **500 responses do not include stack traces**. The middleware
  produces a generic `Internal server error` problem detail; the full
  exception lands in Serilog (with `TraceId`/`SpanId` so the operator
  can pivot). [`ValidationExceptionMiddleware.cs:131`](../src/Ambev.DeveloperEvaluation.WebApi/Middleware/ValidationExceptionMiddleware.cs#L131).
- **Unhandled-exception log lines exclude the query string**, since
  query strings can carry tokens, idempotency keys, or PII. Only the
  route is logged. (Same file, same site.)
- **`/health/*` responses strip exception details outside Dev / Test**
  — never echo connection strings, DNS hostnames, or stack-trace
  fragments to anonymous probes.
- **Login responses are timing-uniform and message-uniform** — no
  oracle for email enumeration.

---

## OWASP API Security Top 10 (2023) — posture

| Item | Control |
|---|---|
| API1 Broken Object Level Authorization | Resource ids are GUIDs (unguessable). Role-based authorization not yet enforced — see "Authorization" above. |
| API2 Broken Authentication | JWT HS256 ≥ 32 bytes, mandatory issuer/audience outside Dev, BCrypt-12 with timing-uniform path, opaque error messages, `auth-strict` rate limit. |
| API3 BOPLA / mass assignment | `CreateUserRequest` omits `Role`/`Status`; the handler hard-codes safe defaults. Asserted by an integration test. |
| API4 Unrestricted resource consumption | Fixed-window rate limits (global + auth-strict); 256-byte cap on Idempotency-Key; 100-item cap per sale; 20-quantity cap per item; bounded list `_size`. |
| API5 Broken function-level authorization | `RequireAuthenticatedUser` fallback policy; explicit `[AllowAnonymous]` on the four public endpoints; no other admin-only surface to break. |
| API6 Unrestricted access to sensitive business flows | Authentication required; rate limit per principal; idempotency on POST. |
| API7 SSRF | The API issues no outbound HTTP calls in any handler. The OpenTelemetry exporter is the only outbound surface and is configured at startup. |
| API8 Security misconfiguration | Fail-fast on missing secrets, on short JWT keys, on missing issuer/audience. Swagger behind explicit flag. CORS denies all by default. HSTS outside Dev. Strict JSON encoder. CSP/X-Frame/Referrer/MIME-sniff headers. |
| API9 Improper inventory management | API versioning via `Asp.Versioning.Mvc`; `api-supported-versions` response header. Single contract surface. |
| API10 Unsafe consumption of APIs | n/a — the service is a producer, not a consumer. |

---

## What's deliberately deferred (and how it's mitigated meanwhile)

| Deferred control | Mitigation today | Bookmark |
|---|---|---|
| Refresh tokens / `jti` / denylist | 8 h lifetime + `JWT_SECRET_KEY` rotation invalidates the fleet. | Roadmap v1.1 |
| HIBP (haveibeenpwned) password screening | Strong password policy in validators + BCrypt-12. | Roadmap v1.1 |
| Stronger Idempotency-Key in-flight protection (Redis `SET NX`) | The current best-effort GET-then-SET narrows the window; the unique index on `SaleNumber` is the source of truth and closes it for the create path. | Roadmap |
| Read-only container FS, `cap_drop: ALL` | Container runs as non-root; secrets externalised. | Deployment-time hardening |
| External pen test | The two-pass internal audit + integration coverage matrix is the current bar. | Pre-production gate |

---

## Reauditoria record

Tracked separately in the project log:

- 2026-05-10 — second pass, **score 87/100, APPROVED for production**
  conditional on:
  1. Purging the early-development Postgres password from the git
     history with `git filter-repo --replace-text` and rotating the
     credential. **Done** — `git log --all -S '<the-old-string>'`
     returns no matches on the rewritten branches.
  2. Documenting the JWT-rotation runbook entry (now in this file).
- Critical issues from the first pass (C1/C2/C3) all addressed:
  authorization fallback policy, mass-assignment fix, secrets fully
  externalised.
- High-severity items addressed in this pass: dummy-hash timing, HSTS
  + `RequireHttpsMetadata`, mandatory issuer/audience, ≥ 32-byte key,
  `/health` error-message redaction, `Swagger:Enabled` flag,
  restrictive CORS, `auth-strict` policy, `KnownProxies`/`KnownNetworks`,
  database port not published.

---

[← back to README](../README.md)
