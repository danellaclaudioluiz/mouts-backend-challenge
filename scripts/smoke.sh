#!/usr/bin/env bash
# End-to-end smoke for every endpoint + every realistic scenario.
# Exits non-zero on the first failure aggregate.

set -uo pipefail

BASE="${BASE:-http://localhost:5119}"
PASS=0
FAIL=0
FAIL_LIST=()
TMPDIR_SMOKE=$(mktemp -d)
trap 'rm -rf "$TMPDIR_SMOKE"' EXIT

if [[ -t 1 ]]; then
  GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[0;33m'; DIM='\033[2m'; NC='\033[0m'
else
  GREEN=''; RED=''; YELLOW=''; DIM=''; NC=''
fi

section() { printf "\n${YELLOW}═══ %s ═══${NC}\n" "$1"; }
expect() {
  local label="$1" actual="$2" wanted="$3"
  if [[ "$actual" == "$wanted" ]]; then
    PASS=$((PASS+1))
    printf "  ${GREEN}✓${NC} %s ${DIM}(%s)${NC}\n" "$label" "$actual"
  else
    FAIL=$((FAIL+1))
    FAIL_LIST+=("$label — wanted=$wanted got=$actual")
    printf "  ${RED}✗${NC} %s ${RED}wanted=%s got=%s${NC}\n" "$label" "$wanted" "$actual"
  fi
}
expect_in_ci() {
  # Case-insensitive substring.
  local label="$1" actual="$2" needle="$3"
  local a_lower=$(printf '%s' "$actual" | tr '[:upper:]' '[:lower:]')
  local n_lower=$(printf '%s' "$needle" | tr '[:upper:]' '[:lower:]')
  if [[ "$a_lower" == *"$n_lower"* ]]; then
    PASS=$((PASS+1))
    printf "  ${GREEN}✓${NC} %s ${DIM}(contains '%s')${NC}\n" "$label" "$needle"
  else
    FAIL=$((FAIL+1))
    FAIL_LIST+=("$label — wanted-contains='$needle' got='${actual:0:200}'")
    printf "  ${RED}✗${NC} %s ${RED}missing '%s'${NC}\n" "$label" "$needle"
    printf "    ${DIM}body: %s${NC}\n" "${actual:0:200}"
  fi
}
expect_in() {
  local label="$1" actual="$2" needle="$3"
  if [[ "$actual" == *"$needle"* ]]; then
    PASS=$((PASS+1))
    printf "  ${GREEN}✓${NC} %s ${DIM}(contains '%s')${NC}\n" "$label" "$needle"
  else
    FAIL=$((FAIL+1))
    FAIL_LIST+=("$label — wanted-contains='$needle' got='${actual:0:200}'")
    printf "  ${RED}✗${NC} %s ${RED}missing '%s'${NC}\n" "$label" "$needle"
    printf "    ${DIM}body: %s${NC}\n" "${actual:0:200}"
  fi
}

# api $method $path $extra-curl-args...
# Writes headers to $TMPDIR_SMOKE/h, body to $TMPDIR_SMOKE/b, and prints the
# raw status code on stdout.
api() {
  local method="$1" path="$2"; shift 2
  curl -s -o "$TMPDIR_SMOKE/b" -D "$TMPDIR_SMOKE/h" -w "%{http_code}" \
    -X "$method" "$BASE$path" "$@"
}
last_body()    { cat "$TMPDIR_SMOKE/b"; }
last_headers() { cat "$TMPDIR_SMOKE/h"; }

json_get() {
  local body="$1" path="$2"
  python -c "import sys,json
try:
  v = json.loads(sys.argv[1])
  for k in sys.argv[2].split('.'):
    v = v[int(k)] if k.isdigit() else v[k]
  print(v if v is not None else '')
except Exception:
  print('')" "$body" "$path" 2>/dev/null
}

# Random suffixes / a real-looking Guid for "unknown id" — Guid.Empty
# trips the Id-NotEmpty validator and returns 400, masking the actual
# 404 paths we want to exercise.
RUN=$(date +%s)
EMAIL="smoke-${RUN}@example.com"
USERNAME="smoke${RUN}"
RANDOM_GUID() { python -c "import uuid; print(uuid.uuid4())"; }
UNKNOWN_ID=$(RANDOM_GUID)
UNKNOWN_ID2=$(RANDOM_GUID)

PSQL_EXEC="docker exec -e PGPASSWORD=dev-only-please-rotate -i ambev_developer_evaluation_database psql -U developer -d developer_evaluation -At"

# ─────────────────────────────────────────────────────────────────────────────
section "1. Health probes (anonymous)"
# ─────────────────────────────────────────────────────────────────────────────
expect "GET /health/live → 200" "$(api GET /health/live)" "200"; expect_in "  body contains Healthy" "$(last_body)" "Healthy"
expect "GET /health/ready → 200" "$(api GET /health/ready)" "200"; expect_in "  body contains Postgres" "$(last_body)" "Postgres"
expect "GET /health → 200" "$(api GET /health)" "200"; expect_in "  body contains Liveness" "$(last_body)" "Liveness"; expect_in "  body contains Readiness" "$(last_body)" "Readiness"

# ─────────────────────────────────────────────────────────────────────────────
section "2. Authorization wall (anonymous on protected routes → 401)"
# ─────────────────────────────────────────────────────────────────────────────
expect "GET /sales anonymous"          "$(api GET /api/v1/sales)" "401"
expect "POST /sales anonymous"         "$(api POST /api/v1/sales -H 'Content-Type: application/json' -d '{}')" "401"
expect "GET /sales/{id} anonymous"     "$(api GET /api/v1/sales/$UNKNOWN_ID)" "401"
expect "DELETE /sales/{id} anonymous"  "$(api DELETE /api/v1/sales/$UNKNOWN_ID)" "401"
expect "GET /users/{id} anonymous"     "$(api GET /api/v1/users/$UNKNOWN_ID)" "401"

# ─────────────────────────────────────────────────────────────────────────────
section "3. Signup (POST /users — anonymous + mass-assignment defence)"
# ─────────────────────────────────────────────────────────────────────────────
expect "POST /users (signup with smuggled role=Admin)" "$(api POST /api/v1/users \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"${USERNAME}\",\"password\":\"Str0ngP@ss!\",\"phone\":\"+5511999998888\",\"email\":\"${EMAIL}\",\"role\":\"Admin\",\"status\":\"Active\"}")" "201"
USER_ID=$(json_get "$(last_body)" "data.id")
expect_in "  response carries user id" "$(last_body)" "\"id\""

expect "POST /users empty body → 400" "$(api POST /api/v1/users -H 'Content-Type: application/json' -d '{"username":"","password":"","phone":"","email":""}')" "400"
expect_in "  problem+json with errors" "$(last_body)" "errors"

expect "POST /users duplicate email → 409" "$(api POST /api/v1/users \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"u${RUN}\",\"password\":\"Str0ngP@ss!\",\"phone\":\"+5511999990000\",\"email\":\"${EMAIL}\"}")" "409"

# ─────────────────────────────────────────────────────────────────────────────
section "4. Login (POST /auth)"
# ─────────────────────────────────────────────────────────────────────────────
expect "POST /auth valid credentials → 200" "$(api POST /api/v1/auth \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"password\":\"Str0ngP@ss!\"}")" "200"
BODY=$(last_body)
TOKEN=$(json_get "$BODY" "data.token")
expect_in "  response includes token" "$BODY" "\"token\""
[[ ${#TOKEN} -gt 100 ]] && { PASS=$((PASS+1)); printf "  ${GREEN}✓${NC} token length looks like JWT ${DIM}(${#TOKEN})${NC}\n"; } || \
  { FAIL=$((FAIL+1)); FAIL_LIST+=("token length=${#TOKEN}"); printf "  ${RED}✗${NC} token length=${#TOKEN}\n"; }

expect "POST /auth wrong password → 401" "$(api POST /api/v1/auth \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"password\":\"WrongPassword!\"}")" "401"
expect_in "  generic 'Invalid credentials' (no enumeration leak)" "$(last_body)" "Invalid credentials"

expect "POST /auth unknown email → 401" "$(api POST /api/v1/auth \
  -H 'Content-Type: application/json' \
  -d '{"email":"nobody@example.com","password":"Whatever1!"}')" "401"
expect_in "  same message as wrong-password" "$(last_body)" "Invalid credentials"

AUTH_H="Authorization: Bearer $TOKEN"

# ─────────────────────────────────────────────────────────────────────────────
section "5. GET /users/{id} — auth required + mass-assignment defence verified"
# ─────────────────────────────────────────────────────────────────────────────
expect "GET /users/{id} authenticated → 200" "$(api GET /api/v1/users/$USER_ID -H "$AUTH_H")" "200"
expect_in "  role=Customer (not Admin smuggled in)" "$(last_body)" "\"role\":\"Customer\""
expect_in "  status=Active" "$(last_body)" "\"status\":\"Active\""

expect "GET /users/{random-unknown} → 404" "$(api GET /api/v1/users/$UNKNOWN_ID -H "$AUTH_H")" "404"
expect "GET /users/not-a-guid → 404 (route constraint)" "$(api GET /api/v1/users/not-a-guid -H "$AUTH_H")" "404"

# ─────────────────────────────────────────────────────────────────────────────
section "6. POST /sales — happy path + ETag + Location + outbox row"
# ─────────────────────────────────────────────────────────────────────────────
SALE_NUMBER="S-SMOKE-${RUN}"
PRODUCT_ID="33333333-3333-3333-3333-333333333333"
PAYLOAD=$(cat <<JSON
{"saleNumber":"${SALE_NUMBER}","saleDate":"2026-05-10T10:00:00Z","customerId":"11111111-1111-1111-1111-111111111111","customerName":"Acme","branchId":"22222222-2222-2222-2222-222222222222","branchName":"Branch 1","items":[{"productId":"${PRODUCT_ID}","productName":"Beer","quantity":5,"unitPrice":10.00}]}
JSON
)
expect "POST /sales → 201" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$PAYLOAD")" "201"
expect_in_ci "  Location header" "$(last_headers)" "Location:"
expect_in_ci "  ETag header" "$(last_headers)" "ETag:"
expect_in "  totalAmount=45.00 (5 × 10 × 90%)" "$(last_body)" "\"totalAmount\":45.00"
expect_in "  discount=5.00 (10% tier)" "$(last_body)" "\"discount\":5.00"
SALE_ID=$(json_get "$(last_body)" "data.id")

expect "POST /sales duplicate SaleNumber → 409" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$PAYLOAD")" "409"

BAD_QTY=$(echo "$PAYLOAD" | python -c "import sys,json; d=json.load(sys.stdin); d['saleNumber']='S-BAD-${RUN}'; d['items'][0]['quantity']=21; print(json.dumps(d))")
expect "POST /sales qty=21 → 400" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$BAD_QTY")" "400"
expect_in "  errors contain Quantity" "$(last_body)" "Quantity"

EMPTY=$(echo "$PAYLOAD" | python -c "import sys,json; d=json.load(sys.stdin); d['saleNumber']='S-EMPTY-${RUN}'; d['items']=[]; print(json.dumps(d))")
expect "POST /sales items=[] → 400" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$EMPTY")" "400"

LONG=$(python -c "import json; print(json.dumps({'saleNumber':'S'*51,'saleDate':'2026-05-10T10:00:00Z','customerId':'11111111-1111-1111-1111-111111111111','customerName':'Acme','branchId':'22222222-2222-2222-2222-222222222222','branchName':'B','items':[{'productId':'33333333-3333-3333-3333-333333333333','productName':'X','quantity':1,'unitPrice':10.0}]}))")
expect "POST /sales SaleNumber 51 chars → 400" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$LONG")" "400"

XSS_PAYLOAD=$(python -c "import json; print(json.dumps({'saleNumber':'S-XSS-${RUN}','saleDate':'2026-05-10T10:00:00Z','customerId':'11111111-1111-1111-1111-111111111111','customerName':'<script>alert(1)</script>','branchId':'22222222-2222-2222-2222-222222222222','branchName':'B','items':[{'productId':'44444444-4444-4444-4444-444444444444','productName':'X','quantity':1,'unitPrice':10.0}]}))")
expect "POST /sales CustomerName=<script> → 201" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$XSS_PAYLOAD")" "201"
BODY=$(last_body)
if [[ "$BODY" == *"<script>"* ]]; then
  FAIL=$((FAIL+1)); FAIL_LIST+=("XSS: raw <script> in response body"); printf "  ${RED}✗${NC} response contains raw <script>\n"
else
  PASS=$((PASS+1)); printf "  ${GREEN}✓${NC} response JSON-encodes <script> (no raw tag)\n"
fi

# ─────────────────────────────────────────────────────────────────────────────
section "7. GET /sales/{id} — read + ETag + 404 + cache"
# ─────────────────────────────────────────────────────────────────────────────
expect "GET /sales/{id} → 200" "$(api GET /api/v1/sales/$SALE_ID -H "$AUTH_H")" "200"
expect_in_ci "  ETag header" "$(last_headers)" "ETag:"
expect_in "  saleNumber matches" "$(last_body)" "\"saleNumber\":\"${SALE_NUMBER}\""

expect "GET /sales/{random-unknown} → 404" "$(api GET /api/v1/sales/$UNKNOWN_ID -H "$AUTH_H")" "404"
expect "GET /sales/not-a-guid → 404 (route constraint)" "$(api GET /api/v1/sales/not-a-guid -H "$AUTH_H")" "404"
expect "GET /sales/{id} second hit → 200 (cache)" "$(api GET /api/v1/sales/$SALE_ID -H "$AUTH_H")" "200"

# ─────────────────────────────────────────────────────────────────────────────
section "8. GET /sales — list, pagination, filters, ordering"
# ─────────────────────────────────────────────────────────────────────────────
expect "GET /sales paginated → 200" "$(api GET '/api/v1/sales?_page=1&_size=10' -H "$AUTH_H")" "200"
expect_in "  totalCount field" "$(last_body)" "totalCount"
expect_in "  data array" "$(last_body)" "\"data\":["
expect_in "  currentPage=1" "$(last_body)" "\"currentPage\":1"

expect "GET /sales _size=0 → 400"          "$(api GET '/api/v1/sales?_size=0' -H "$AUTH_H")" "400"
expect "GET /sales _size=101 → 400"        "$(api GET '/api/v1/sales?_size=101' -H "$AUTH_H")" "400"
expect "GET /sales _order=password → 400"  "$(api GET '/api/v1/sales?_order=password+desc' -H "$AUTH_H")" "400"
expect "GET /sales _page + _cursor → 400"  "$(api GET '/api/v1/sales?_page=2&_cursor=abc' -H "$AUTH_H")" "400"
expect "GET /sales _page=999 → 200 empty"  "$(api GET '/api/v1/sales?_page=999&_size=10' -H "$AUTH_H")" "200"
expect "GET /sales?customerId=…"           "$(api GET '/api/v1/sales?customerId=11111111-1111-1111-1111-111111111111' -H "$AUTH_H")" "200"
expect "GET /sales?isCancelled=false"      "$(api GET '/api/v1/sales?isCancelled=false' -H "$AUTH_H")" "200"
expect "GET /sales?_order=totalAmount desc" "$(api GET '/api/v1/sales?_order=totalAmount+desc' -H "$AUTH_H")" "200"

# ─────────────────────────────────────────────────────────────────────────────
section "9. PUT /sales/{id} — diff update + If-Match"
# ─────────────────────────────────────────────────────────────────────────────
UPDATE_PAYLOAD=$(cat <<JSON
{"saleDate":"2026-05-10T10:00:00Z","customerId":"11111111-1111-1111-1111-111111111111","customerName":"Updated","branchId":"22222222-2222-2222-2222-222222222222","branchName":"B","items":[{"productId":"${PRODUCT_ID}","productName":"Beer","quantity":7,"unitPrice":10.00}]}
JSON
)
expect "PUT /sales/{id} → 200" "$(api PUT /api/v1/sales/$SALE_ID -H "$AUTH_H" -H 'Content-Type: application/json' -d "$UPDATE_PAYLOAD")" "200"
expect_in_ci "  new ETag emitted" "$(last_headers)" "ETag:"
expect_in "  totalAmount=63.00 (7 × 10 × 90%)" "$(last_body)" "\"totalAmount\":63.00"

expect "PUT /sales/{id} stale If-Match → 412" "$(api PUT /api/v1/sales/$SALE_ID -H "$AUTH_H" -H 'If-Match: "deadbeef"' -H 'Content-Type: application/json' -d "$UPDATE_PAYLOAD")" "412"
expect "PUT /sales/{id} If-Match=* → 200 (bypass)" "$(api PUT /api/v1/sales/$SALE_ID -H "$AUTH_H" -H 'If-Match: *' -H 'Content-Type: application/json' -d "$UPDATE_PAYLOAD")" "200"
expect "PUT /sales/{random-unknown} → 404" "$(api PUT /api/v1/sales/$UNKNOWN_ID -H "$AUTH_H" -H 'Content-Type: application/json' -d "$UPDATE_PAYLOAD")" "404"

# ─────────────────────────────────────────────────────────────────────────────
section "10. PATCH /sales/{id}/cancel — soft cancel + idempotency"
# ─────────────────────────────────────────────────────────────────────────────
CANCEL_NUMBER="S-CANCEL-${RUN}"
CANCEL_PAYLOAD=$(echo "$PAYLOAD" | python -c "import sys,json; d=json.load(sys.stdin); d['saleNumber']='${CANCEL_NUMBER}'; d['items'][0]['productId']='55555555-5555-5555-5555-555555555555'; print(json.dumps(d))")
expect "POST /sales (for cancel test) → 201" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$CANCEL_PAYLOAD")" "201"
CANCEL_ID=$(json_get "$(last_body)" "data.id")

expect "PATCH /sales/{id}/cancel → 200" "$(api PATCH /api/v1/sales/$CANCEL_ID/cancel -H "$AUTH_H")" "200"
expect_in_ci "  ETag emitted" "$(last_headers)" "ETag:"
expect_in "  isCancelled=true" "$(last_body)" "\"isCancelled\":true"

expect "PATCH /sales/{id}/cancel (idempotent) → 200" "$(api PATCH /api/v1/sales/$CANCEL_ID/cancel -H "$AUTH_H")" "200"
expect "PATCH /sales/{random-unknown}/cancel → 404" "$(api PATCH /api/v1/sales/$UNKNOWN_ID/cancel -H "$AUTH_H")" "404"

# ─────────────────────────────────────────────────────────────────────────────
section "11. PATCH /sales/{id}/items/{itemId}/cancel — line-level cancel"
# ─────────────────────────────────────────────────────────────────────────────
ITEM_NUMBER="S-ITEM-${RUN}"
ITEM_PAYLOAD=$(python -c "import json; print(json.dumps({'saleNumber':'${ITEM_NUMBER}','saleDate':'2026-05-10T10:00:00Z','customerId':'11111111-1111-1111-1111-111111111111','customerName':'Acme','branchId':'22222222-2222-2222-2222-222222222222','branchName':'B','items':[{'productId':'66666666-6666-6666-6666-666666666666','productName':'Beer','quantity':5,'unitPrice':10.0},{'productId':'77777777-7777-7777-7777-777777777777','productName':'Ale','quantity':4,'unitPrice':20.0}]}))")
expect "POST /sales (2 items) → 201" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$ITEM_PAYLOAD")" "201"
TWO_ITEM_SALE=$(json_get "$(last_body)" "data.id")
FIRST_ITEM=$(json_get "$(last_body)" "data.items.0.id")

expect "PATCH /items/{itemId}/cancel → 200" "$(api PATCH /api/v1/sales/$TWO_ITEM_SALE/items/$FIRST_ITEM/cancel -H "$AUTH_H")" "200"
expect_in "  totalAmount recalculated (only 2nd item remains)" "$(last_body)" "\"totalAmount\":72.00"
expect_in "  activeItemsCount=1" "$(last_body)" "\"activeItemsCount\":1"

expect "PATCH /items/{itemId}/cancel (idempotent) → 200" "$(api PATCH /api/v1/sales/$TWO_ITEM_SALE/items/$FIRST_ITEM/cancel -H "$AUTH_H")" "200"
expect "PATCH /items/{random-unknown}/cancel → 400" "$(api PATCH /api/v1/sales/$TWO_ITEM_SALE/items/$UNKNOWN_ID/cancel -H "$AUTH_H")" "400"

# ─────────────────────────────────────────────────────────────────────────────
section "12. Idempotency-Key (POST /sales)"
# ─────────────────────────────────────────────────────────────────────────────
IDEM_KEY="idem-smoke-${RUN}"
IDEM_PAYLOAD=$(python -c "import json; print(json.dumps({'saleNumber':'S-IDEM-${RUN}','saleDate':'2026-05-10T10:00:00Z','customerId':'11111111-1111-1111-1111-111111111111','customerName':'Acme','branchId':'22222222-2222-2222-2222-222222222222','branchName':'B','items':[{'productId':'88888888-8888-8888-8888-888888888888','productName':'X','quantity':1,'unitPrice':10.0}]}))")
expect "POST /sales with Idempotency-Key → 201" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -H "Idempotency-Key: $IDEM_KEY" -d "$IDEM_PAYLOAD")" "201"
BODY_FIRST=$(last_body)

expect "POST /sales replay same key+body → 201 (cached)" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -H "Idempotency-Key: $IDEM_KEY" -d "$IDEM_PAYLOAD")" "201"
BODY_REPLAY=$(last_body)
[[ "$BODY_FIRST" == "$BODY_REPLAY" ]] && { PASS=$((PASS+1)); printf "  ${GREEN}✓${NC} replayed body byte-equal to original\n"; } || \
  { FAIL=$((FAIL+1)); FAIL_LIST+=("idempotency replay body differs"); printf "  ${RED}✗${NC} replayed body differs from original\n"; }

IDEM_PAYLOAD2=$(echo "$IDEM_PAYLOAD" | python -c "import sys,json; d=json.load(sys.stdin); d['saleNumber']='S-IDEM-OTHER-${RUN}'; print(json.dumps(d))")
expect "POST /sales same key + different body → 422" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -H "Idempotency-Key: $IDEM_KEY" -d "$IDEM_PAYLOAD2")" "422"

OVER_KEY=$(python -c "print('k'*257)")
expect "POST /sales key > 256 chars → 400" "$(api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -H "Idempotency-Key: $OVER_KEY" -d "$IDEM_PAYLOAD")" "400"

# ─────────────────────────────────────────────────────────────────────────────
section "13. DELETE /sales/{id} — cascade + If-Match"
# ─────────────────────────────────────────────────────────────────────────────
DELETE_NUMBER="S-DELETE-${RUN}"
DELETE_PAYLOAD=$(echo "$PAYLOAD" | python -c "import sys,json; d=json.load(sys.stdin); d['saleNumber']='${DELETE_NUMBER}'; d['items'][0]['productId']='99999999-9999-9999-9999-999999999999'; print(json.dumps(d))")
api POST /api/v1/sales -H "$AUTH_H" -H 'Content-Type: application/json' -d "$DELETE_PAYLOAD" >/dev/null
DELETE_ID=$(json_get "$(last_body)" "data.id")

expect "DELETE /sales/{id} stale If-Match → 412" "$(api DELETE /api/v1/sales/$DELETE_ID -H "$AUTH_H" -H 'If-Match: "deadbeef"')" "412"
expect "DELETE /sales/{id} → 204 NoContent"      "$(api DELETE /api/v1/sales/$DELETE_ID -H "$AUTH_H")" "204"
expect "GET /sales/{id} after delete → 404"      "$(api GET /api/v1/sales/$DELETE_ID -H "$AUTH_H")" "404"
expect "DELETE /sales/{random-unknown} → 404"    "$(api DELETE /api/v1/sales/$UNKNOWN_ID2 -H "$AUTH_H")" "404"

# ─────────────────────────────────────────────────────────────────────────────
section "14. Outbox — events were written for each mutation"
# ─────────────────────────────────────────────────────────────────────────────
OUTBOX_TYPES=$($PSQL_EXEC -c 'SELECT DISTINCT "EventType" FROM "OutboxMessages" ORDER BY "EventType";' 2>/dev/null | tr '\n' ' ' || echo "")

expect_in "outbox has sale.created.v1"       "$OUTBOX_TYPES" "sale.created.v1"
expect_in "outbox has sale.modified.v1"      "$OUTBOX_TYPES" "sale.modified.v1"
expect_in "outbox has sale.cancelled.v1"     "$OUTBOX_TYPES" "sale.cancelled.v1"
expect_in "outbox has sale.item_cancelled.v1" "$OUTBOX_TYPES" "sale.item_cancelled.v1"

# Most rows should be processed by the dispatcher (polls every 5s).
sleep 6
PROCESSED=$($PSQL_EXEC -c 'SELECT count(*) FROM "OutboxMessages" WHERE "ProcessedAt" IS NOT NULL;' 2>/dev/null | tr -d ' \n')
TOTAL=$($PSQL_EXEC -c 'SELECT count(*) FROM "OutboxMessages";' 2>/dev/null | tr -d ' \n')
if [[ -n "$PROCESSED" && -n "$TOTAL" && "${PROCESSED:-0}" -gt 0 ]]; then
  PASS=$((PASS+1)); printf "  ${GREEN}✓${NC} dispatcher processed %s of %s outbox rows\n" "$PROCESSED" "$TOTAL"
else
  FAIL=$((FAIL+1)); FAIL_LIST+=("dispatcher idle: processed='$PROCESSED' total='$TOTAL'"); printf "  ${RED}✗${NC} dispatcher processed='%s' total='%s'\n" "$PROCESSED" "$TOTAL"
fi

# Verify envelope shape on one row (eventId, eventType, occurredAt, data).
SAMPLE_PAYLOAD=$($PSQL_EXEC -c "SELECT \"Payload\" FROM \"OutboxMessages\" WHERE \"EventType\" = 'sale.created.v1' LIMIT 1;" 2>/dev/null)
expect_in "outbox envelope carries eventId"   "$SAMPLE_PAYLOAD" "eventId"
expect_in "outbox envelope carries eventType" "$SAMPLE_PAYLOAD" "eventType"
expect_in "outbox envelope carries data block" "$SAMPLE_PAYLOAD" "\"data\""

# ─────────────────────────────────────────────────────────────────────────────
section "15. DELETE /users/{id}"
# ─────────────────────────────────────────────────────────────────────────────
expect "DELETE /users/{id} → 200"     "$(api DELETE /api/v1/users/$USER_ID -H "$AUTH_H")" "200"
expect "GET /users/{deleted} → 404"   "$(api GET /api/v1/users/$USER_ID -H "$AUTH_H")" "404"

echo
TOTAL_RAN=$((PASS+FAIL))
if [[ $FAIL -eq 0 ]]; then
  printf "${GREEN}═══ ALL %d CHECKS PASSED ═══${NC}\n" "$TOTAL_RAN"
  exit 0
else
  printf "${RED}═══ %d / %d FAILED ═══${NC}\n" "$FAIL" "$TOTAL_RAN"
  for f in "${FAIL_LIST[@]}"; do printf "  ${RED}•${NC} %s\n" "$f"; done
  exit 1
fi
