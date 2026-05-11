# Ops Runbook — Sales API

What an on-call engineer needs to know in the first 15 minutes of a page.
Each scenario starts with the **symptom** (what the alert / customer
reports), then the **immediate mitigation**, then the **root-cause
hunt** with the exact commands.

> All commands assume `cwd =` the repo root. Replace
> `ambev_developer_evaluation_database` with the production container /
> pod name where applicable.

---

## 1. Database is down (`/health/ready` returns 503)

**Symptom.** Loadbalancer pulls the pod, `aggregate health` flips to
Unhealthy, `Postgres` probe in the JSON shows `status: "Unhealthy"`.

**Immediate mitigation.**

1. Confirm it's the DB, not the app — the WebApi process should still be
   up (kubernetes liveness probe passes, only readiness fails):
   ```bash
   curl -sS http://api/health/live    # → 200
   curl -sS http://api/health/ready   # → 503 + Postgres Unhealthy
   ```
2. If Postgres is managed (RDS, Cloud SQL): check the provider console
   for a maintenance / failover event. If self-hosted, `pg_isready` from
   the WebApi pod:
   ```bash
   kubectl exec -it deploy/sales-api -- pg_isready -h $DB_HOST -p 5432
   ```
3. Once DB is back, no action needed on the WebApi side — readiness
   probe self-heals on the next tick.

**Root-cause hunt.**

- Check Postgres logs for the timestamp of disconnection.
- Verify connection pool exhaustion isn't the real cause:
  ```sql
  SELECT count(*) FROM pg_stat_activity WHERE datname = 'developer_evaluation';
  ```
  If close to `max_connections`, the WebApi's pool config (default
  `Minimum Pool Size=10;Maximum Pool Size=200`) may need tuning, or a
  long-running query is hogging connections (find it via
  `pg_stat_activity` with `state = 'active'`).

---

## 2. Outbox backlog growing ("events are stuck")

**Symptom.** Downstream consumer hasn't seen new `sale.created.v1` events
for N minutes; dashboard shows outbox `pending` count climbing.

**Immediate mitigation.**

1. Confirm the dispatcher is actually running — its startup log:
   ```
   Outbox dispatcher started; LISTEN 'outbox_pending' + poll fallback every 00:00:05
   ```
   If absent in the last hour, the BackgroundService crashed. Restart the
   pod.
2. Check for poison-pilled rows (dead-lettered at 10 retries):
   ```sql
   SELECT count(*), max("Attempts")
     FROM "OutboxMessages"
    WHERE "ProcessedAt" IS NULL AND "Attempts" >= 10;
   ```
   Quarantined rows DO NOT get redelivered automatically — they need a
   human review.

**Root-cause hunt.**

```sql
-- What's pending, oldest first, with the last error if any:
SELECT "Id", "EventType", "OccurredAt", "Attempts", "LastError"
  FROM "OutboxMessages"
 WHERE "ProcessedAt" IS NULL
 ORDER BY "OccurredAt"
 LIMIT 50;

-- How fast we're draining:
SELECT
  count(*) FILTER (WHERE "ProcessedAt" IS NULL) AS pending,
  count(*) FILTER (WHERE "ProcessedAt" >= now() - interval '1 minute') AS dispatched_last_min
FROM "OutboxMessages";
```

**Replay a quarantined row** after fixing the bug downstream:

```sql
UPDATE "OutboxMessages"
   SET "Attempts" = 0, "LastError" = NULL, "LockedUntil" = NULL
 WHERE "Id" = '<the-id>';
-- The dispatcher will pick it up on the next tick (≤ 5 s).
```

---

## 3. JWT secret leaked / rotated

**Symptom.** A `JWT_SECRET_KEY` was exposed (chat, log, screenshot,
ex-employee laptop) OR routine rotation is due (recommended quarterly).

**Immediate mitigation — every issued token becomes invalid.** Plan a
short outage window or roll the keys with overlap (below).

**Hard rotation (immediate invalidation, brief 401 storm).**

1. Generate a new key — minimum 32 random bytes:
   ```bash
   openssl rand -base64 32      # → base64 string
   ```
2. Update the secret in your secret store (Key Vault / GCP Secret
   Manager / `kubectl create secret`).
3. Roll the deployment. From the new pod's first second, the OLD key no
   longer verifies any token; every active client gets 401 on its next
   request and must re-authenticate via `POST /api/v1/auth`.
4. Audit `Users` for unexpected `Admin` roles created during the
   window — if the leak preceded the rotation, the attacker may have
   minted long-lived tokens already:
   ```sql
   SELECT "Id", "Email", "Role", "CreatedAt"
     FROM "Users"
    WHERE "Role" = 'Admin' AND "CreatedAt" > now() - interval '7 days';
   ```

**Soft rotation (no outage, requires code change).** Out of scope for
this version of the API — would require adding a SECONDARY JWT key
that validates legacy tokens during a grace window. Tracked under
"future work" in [security.md](security.md).

---

## 4. Cache (Redis) is degraded

**Symptom.** Latency on `GET /sales/{id}` doubles; logs show
`Cache GET failed for sale {id} — falling back to DB`.

**Status of the API.** Green. The cache is **best-effort** by design —
every operation has a try/catch that falls through to the DB. So a Redis
outage causes elevated latency, not 5xx.

**Immediate mitigation.** Restart Redis / check the provider's status.
No action needed on the API.

**Root-cause hunt.**

```bash
# From a WebApi pod:
redis-cli -h $REDIS_HOST -a $REDIS_PASSWORD ping
redis-cli -h $REDIS_HOST -a $REDIS_PASSWORD info memory
```

If `used_memory` is at the configured cap, the cache is evicting useful
keys — bump memory or shorten TTLs (`DistributedSaleReadCache.Ttl`, the
24h Idempotency-Key window in `IdempotencyMiddleware`).

---

## 5. Rate-limit storm (429s in customer logs)

**Symptom.** Customer reports 429 from their own pod, even though
they're sending modest traffic.

**Hunt.**

1. Confirm the partition key isn't collapsing — the limiter partitions
   by `NameIdentifier` claim first, then `RemoteIpAddress`. If multiple
   customer pods share a NAT and request anonymously, they share a
   bucket.
2. Check `ForwardedHeaders:KnownProxies` is populated for production —
   without it, every request from your load balancer collapses into a
   single rate-limit partition (the LB's IP).

**Tuning.** `RateLimit:PermitLimit` and `RateLimit:WindowSeconds` are
config-driven; no redeploy needed in environments that hot-reload
config. The `auth-strict` policy (`/api/v1/auth`) defaults to 5/min/IP
to slow brute force — tune via `RateLimit:AuthPermitLimit`.

---

## 6. Idempotency-Key cache full / replays not honoured

**Symptom.** Client reports two charges for the same idempotency key.

**Hunt.** The middleware uses `IDistributedCache` — Redis when
configured, in-process otherwise. If Redis was unavailable and the
WebApi was running with `AddDistributedMemoryCache` fallback, the
in-process cache is **per-pod**, so two pods can each accept the same
key once.

**Mitigation.** Always run with Redis in production (set
`ConnectionStrings:Redis`). The fallback exists for local dev only;
production startup should fail fast if Redis is unset — file a follow-up
issue.

---

## 7. Suspected security incident (mass-assignment, auth bypass)

1. Snapshot the current state (DB dump, Redis dump, app logs).
2. Rotate `JWT_SECRET_KEY` (see § 3) AND `POSTGRES_PASSWORD` (force a
   pool refresh by rolling the deployment).
3. `SELECT * FROM "Users" WHERE "Role" = 'Admin' OR "Status" =
   'Suspended'` and compare to the expected baseline.
4. Audit the last 24h of logins:
   ```sql
   -- The app does not currently persist a login audit table — derive
   -- from web access logs. If this becomes a recurring need, add a
   -- "login_events" table with (UserId, At, IpAddress, UserAgent, Outcome).
   ```
5. Open a post-incident ticket.

---

## 8. Useful queries

```sql
-- Top 10 sales by total amount in the last hour.
SELECT "Id", "SaleNumber", "TotalAmount", "CustomerName"
  FROM "Sales"
 WHERE "CreatedAt" > now() - interval '1 hour'
 ORDER BY "TotalAmount" DESC
 LIMIT 10;

-- Outbox dispatch rate (per minute):
SELECT date_trunc('minute', "ProcessedAt") AS minute, count(*)
  FROM "OutboxMessages"
 WHERE "ProcessedAt" > now() - interval '1 hour'
 GROUP BY 1 ORDER BY 1;

-- Slowest queries seen by pg_stat_statements (if extension is enabled):
SELECT calls, mean_exec_time, query
  FROM pg_stat_statements
 ORDER BY mean_exec_time DESC
 LIMIT 10;
```
