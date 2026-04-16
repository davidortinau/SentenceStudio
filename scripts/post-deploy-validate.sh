#!/usr/bin/env bash
# post-deploy-validate.sh — Post-deploy validation for SentenceStudio Azure deployments
#
# Run IMMEDIATELY after every `azd deploy` or `aspire deploy`.
# Exit 0 = all checks passed (deploy is valid).
# Exit 1 = one or more checks failed (deploy is BROKEN, investigate immediately).
#
# Usage:
#   ./scripts/post-deploy-validate.sh                     # Run all automated checks
#   ./scripts/post-deploy-validate.sh --skip-wait         # Skip the 30s startup wait
#   ./scripts/post-deploy-validate.sh --phase1-only       # Infrastructure checks only
#
# Environment variables (optional):
#   DEPLOY_TEST_EMAIL    — Test account email (default: deploy-test@sentencestudio.app)
#   DEPLOY_TEST_PASSWORD — Test account password (no default — Phase 2 auth tests skipped if unset)
#   SKIP_PHASE2_AUTH     — Set to "1" to skip auth smoke tests

set -uo pipefail

# ── Configuration ────────────────────────────────────────────────────────────

RG="rg-sstudio-prod"
API_BASE="https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io"
WEBAPP_URL="https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io"
MARKETING_URL="https://www.sentencestudio.com"
CONTAINER_APPS="api webapp marketing workers"

DEPLOY_TEST_EMAIL="${DEPLOY_TEST_EMAIL:-deploy-test@sentencestudio.app}"
DEPLOY_TEST_PASSWORD="${DEPLOY_TEST_PASSWORD:-}"

SKIP_WAIT="${1:-}"
PHASE1_ONLY=false
if [[ "$SKIP_WAIT" == "--phase1-only" ]]; then
  PHASE1_ONLY=true
  SKIP_WAIT=""
fi

# ── Colors & Counters ───────────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m'

PASS_COUNT=0
FAIL_COUNT=0
SKIP_COUNT=0
WARN_COUNT=0

pass()  { echo -e "  ${GREEN}PASS${NC}: $1"; PASS_COUNT=$((PASS_COUNT + 1)); }
fail()  { echo -e "  ${RED}FAIL${NC}: $1"; FAIL_COUNT=$((FAIL_COUNT + 1)); }
skip()  { echo -e "  ${YELLOW}SKIP${NC}: $1"; SKIP_COUNT=$((SKIP_COUNT + 1)); }
warn()  { echo -e "  ${YELLOW}WARN${NC}: $1"; WARN_COUNT=$((WARN_COUNT + 1)); }
phase() { echo -e "\n${CYAN}${BOLD}=== $1 ===${NC}\n"; }

# ── Helper: Check HTTP endpoint ─────────────────────────────────────────────

check_endpoint() {
  local URL="$1" METHOD="${2:-GET}" EXPECTED="$3" LABEL="$4" BODY="${5:-}"
  local ACTUAL

  if [ "$METHOD" = "POST" ]; then
    ACTUAL=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$URL" \
      -H "Content-Type: application/json" -d "$BODY" --max-time 15 2>/dev/null)
  else
    ACTUAL=$(curl -s -o /dev/null -w "%{http_code}" -L "$URL" --max-time 15 2>/dev/null)
  fi

  if echo "$EXPECTED" | grep -q "$ACTUAL"; then
    pass "$LABEL -> HTTP $ACTUAL"
    return 0
  else
    fail "$LABEL -> HTTP $ACTUAL (expected $EXPECTED)"
    return 1
  fi
}

# ── Banner ───────────────────────────────────────────────────────────────────

echo ""
echo "=============================================="
echo "     POST-DEPLOY VALIDATION"
echo "=============================================="
echo "  Resource Group: $RG"
echo "  API:     $API_BASE"
echo "  WebApp:  $WEBAPP_URL"
echo "  Time:    $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
echo "=============================================="

# ── Startup Wait ─────────────────────────────────────────────────────────────

if [[ "$SKIP_WAIT" != "--skip-wait" ]]; then
  echo ""
  echo "Waiting 30 seconds for containers to start and migrations to run..."
  sleep 30
fi

# ══════════════════════════════════════════════════════════════════════════════
# PHASE 1: INFRASTRUCTURE HEALTH
# ══════════════════════════════════════════════════════════════════════════════

phase "Phase 1: Infrastructure Health"

# ── 1.1 Revision Status ─────────────────────────────────────────────────────

echo "--- 1.1 Revision Status ---"
for APP in $CONTAINER_APPS; do
  RUNNING_STATE=$(az containerapp revision list \
    --name "$APP" --resource-group "$RG" \
    --query "[?properties.trafficWeight > \`0\`].properties.runningState | [0]" \
    -o tsv 2>/dev/null || echo "ERROR")

  if [ "$RUNNING_STATE" = "Running" ]; then
    pass "$APP active revision is Running"
  elif [ "$RUNNING_STATE" = "ERROR" ] || [ -z "$RUNNING_STATE" ]; then
    # Some apps (workers) may not have revisions visible or may be scaled to zero
    warn "$APP revision status could not be determined (may be scaled to zero)"
  else
    fail "$APP active revision state: $RUNNING_STATE"
  fi
done

# ── 1.2 Active Revision = Latest ────────────────────────────────────────────

echo ""
echo "--- 1.2 Active Revision = Latest ---"
for APP in api webapp; do
  LATEST=$(az containerapp revision list \
    --name "$APP" --resource-group "$RG" \
    --query "sort_by(@, &properties.createdTime)[-1].name" -o tsv 2>/dev/null || echo "")
  ACTIVE=$(az containerapp revision list \
    --name "$APP" --resource-group "$RG" \
    --query "[?properties.trafficWeight > \`0\`].name | [0]" -o tsv 2>/dev/null || echo "")

  if [ -z "$LATEST" ] || [ -z "$ACTIVE" ]; then
    warn "$APP could not determine revisions"
  elif [ "$LATEST" = "$ACTIVE" ]; then
    pass "$APP active revision ($ACTIVE) is the latest"
  else
    fail "$APP active=$ACTIVE but latest=$LATEST — traffic may be hitting OLD code!"
  fi
done

# ── 1.3 No Crash Loops ──────────────────────────────────────────────────────

echo ""
echo "--- 1.3 No Crash Loops ---"
CRASH_INDICATORS=$(az containerapp logs show \
  --name api --resource-group "$RG" \
  --tail 100 --type system 2>/dev/null | grep -ciE "backoff|crash|oom|killed|exit code [^0]|unhealthy" || echo "0")

if [ "$CRASH_INDICATORS" -eq 0 ] 2>/dev/null; then
  pass "No crash indicators in recent API system logs"
else
  fail "Found $CRASH_INDICATORS crash-related log entries in API"
fi

# ── 1.4 Database Connectivity (indirect via API) ────────────────────────────

echo ""
echo "--- 1.4 Database Connectivity ---"
DB_CHECK_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API_BASE/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"healthcheck@test.invalid","password":"x"}' --max-time 15 2>/dev/null)

if echo "$DB_CHECK_CODE" | grep -qE "^(400|401|404)$"; then
  pass "API reached database (auth returned HTTP $DB_CHECK_CODE)"
elif echo "$DB_CHECK_CODE" | grep -qE "^(500|502|503|504|000)$"; then
  fail "API cannot reach database (HTTP $DB_CHECK_CODE — likely DB connection failure)"
else
  warn "Unexpected status from login endpoint: HTTP $DB_CHECK_CODE"
fi

# ── 1.5 Endpoint Status Codes ───────────────────────────────────────────────

echo ""
echo "--- 1.5 Endpoint Status Codes ---"
check_endpoint "$WEBAPP_URL" GET "200" "WebApp homepage"
check_endpoint "$API_BASE/weatherforecast" GET "200" "API weatherforecast"
check_endpoint "$API_BASE/api/v1/auth/bootstrap" GET "401" "API bootstrap (auth guard)"
check_endpoint "$API_BASE/api/auth/login" POST "400|401" "API login endpoint" '{"email":"x","password":"x"}'

# ── 1.6 EF Core Migrations ──────────────────────────────────────────────────

echo ""
echo "--- 1.6 EF Core Migrations ---"
MIGRATION_LOGS=$(az containerapp logs show \
  --name api --resource-group "$RG" \
  --tail 50 2>/dev/null | grep -ciE "applying migration|database is up to date|migrat" || echo "0")

if [ "$MIGRATION_LOGS" -gt 0 ] 2>/dev/null; then
  pass "Migration-related log entries found ($MIGRATION_LOGS)"
else
  warn "No migration log entries found (may have scrolled out of recent logs)"
fi

# ══════════════════════════════════════════════════════════════════════════════
# PHASE 2: FUNCTIONAL SMOKE TEST
# ══════════════════════════════════════════════════════════════════════════════

if [ "$PHASE1_ONLY" = true ]; then
  phase "Phase 2-4: Skipped (--phase1-only mode)"
else

  phase "Phase 2: Functional Smoke Test"

  # ── 2.1 Auth Flow ──────────────────────────────────────────────────────────

  echo "--- 2.1 Auth Flow ---"
  JWT=""

  if [ -n "$DEPLOY_TEST_PASSWORD" ] && [ "${SKIP_PHASE2_AUTH:-}" != "1" ]; then
    LOGIN_RESPONSE=$(curl -s -X POST "$API_BASE/api/auth/login" \
      -H "Content-Type: application/json" \
      -d "{\"email\":\"$DEPLOY_TEST_EMAIL\",\"password\":\"$DEPLOY_TEST_PASSWORD\"}" \
      --max-time 15 2>/dev/null)

    JWT=$(echo "$LOGIN_RESPONSE" | jq -r '.token // empty' 2>/dev/null)
    if [ -n "$JWT" ]; then
      pass "Login succeeded for test account, JWT obtained"
    else
      ERROR_MSG=$(echo "$LOGIN_RESPONSE" | jq -r '.message // .error // "unknown"' 2>/dev/null)
      fail "Login failed for test account: $ERROR_MSG"
    fi
  else
    skip "Auth flow (DEPLOY_TEST_PASSWORD not set or SKIP_PHASE2_AUTH=1)"
  fi

  # ── 2.2 Protected Endpoint with JWT ────────────────────────────────────────

  echo ""
  echo "--- 2.2 Protected Endpoint ---"
  if [ -n "$JWT" ]; then
    BOOTSTRAP_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
      "$API_BASE/api/v1/auth/bootstrap" \
      -H "Authorization: Bearer $JWT" --max-time 15 2>/dev/null)

    if [ "$BOOTSTRAP_CODE" = "200" ]; then
      pass "Bootstrap endpoint returned 200 with JWT"
    else
      fail "Bootstrap endpoint returned HTTP $BOOTSTRAP_CODE with valid JWT"
    fi
  else
    skip "Protected endpoint test (no JWT available)"
  fi

  # ── 2.3 WebApp Renders ─────────────────────────────────────────────────────

  echo ""
  echo "--- 2.3 WebApp Content ---"
  WEBAPP_BODY=$(curl -sL "$WEBAPP_URL" --max-time 15 2>/dev/null)

  if echo "$WEBAPP_BODY" | grep -q "_framework/blazor"; then
    pass "WebApp Blazor framework loaded"
  else
    fail "WebApp did not return Blazor framework references"
  fi

  if echo "$WEBAPP_BODY" | grep -qi "sentencestudio\|sentence studio\|login\|sign in"; then
    pass "WebApp contains expected content"
  else
    fail "WebApp content looks wrong (possible blank page or error)"
  fi

  # ══════════════════════════════════════════════════════════════════════════════
  # PHASE 4: REGRESSION CHECK
  # ══════════════════════════════════════════════════════════════════════════════

  phase "Phase 4: Regression Check"

  echo "--- 4.1 Core API Endpoints ---"
  check_endpoint "$API_BASE/weatherforecast" GET "200" "Weatherforecast (basic routing)"
  check_endpoint "$API_BASE/api/v1/auth/bootstrap" GET "401" "Bootstrap (auth guard)"
  check_endpoint "$API_BASE/api/auth/login" POST "400|401" "Login (auth endpoint)" '{"email":"x","password":"x"}'
  check_endpoint "$API_BASE/api/auth/register" POST "400" "Register (validation)" '{"email":"bad","password":"x"}'

  echo ""
  echo "--- 4.3 Marketing Site ---"
  check_endpoint "$MARKETING_URL" GET "200" "Marketing site (sentencestudio.com)"

fi

# ══════════════════════════════════════════════════════════════════════════════
# SUMMARY
# ══════════════════════════════════════════════════════════════════════════════

echo ""
echo "=============================================="
echo "     VALIDATION SUMMARY"
echo "=============================================="
echo -e "  ${GREEN}PASS${NC}: $PASS_COUNT"
echo -e "  ${RED}FAIL${NC}: $FAIL_COUNT"
echo -e "  ${YELLOW}SKIP${NC}: $SKIP_COUNT"
echo -e "  ${YELLOW}WARN${NC}: $WARN_COUNT"
echo "=============================================="

if [ "$FAIL_COUNT" -gt 0 ]; then
  echo ""
  echo -e "  ${RED}${BOLD}DEPLOY VALIDATION FAILED${NC}"
  echo ""
  echo "  $FAIL_COUNT check(s) failed. The deploy is NOT confirmed working."
  echo "  Investigate immediately. Do NOT claim the deploy succeeded."
  echo "  See docs/specs/post-deploy-validation.md for remediation guidance."
  echo "=============================================="
  exit 1
else
  echo ""
  echo -e "  ${GREEN}${BOLD}DEPLOY VALIDATION PASSED${NC}"
  echo ""
  echo "  All automated checks passed."
  if [ "$SKIP_COUNT" -gt 0 ]; then
    echo "  $SKIP_COUNT check(s) skipped — review manually if needed."
  fi
  if [ "$WARN_COUNT" -gt 0 ]; then
    echo "  $WARN_COUNT warning(s) — review but not blocking."
  fi
  echo ""
  echo "  NOTE: Phase 3 (change-specific validation) must still be done manually."
  echo "  What changed in this deploy? Verify it's live."
  echo "=============================================="
  exit 0
fi
