#!/usr/bin/env bash
set -uo pipefail
# ---------------------------------------------------------------------------
# post-aspire-restore.sh — Read-only health gate for a local Aspire stack.
#
# Validates that WebApp, API, and DB volume are healthy after an aspire run/
# restore. NEVER restarts, stops, removes, or mutates anything.
#
# Exit 0 = all checks passed.
# Exit 1 = one or more checks failed.
#
# Usage:
#   ./scripts/post-aspire-restore.sh [OPTIONS]
#
# Options:
#   --webapp-url URL       WebApp public URL (default: https://localhost:7071)
#   --api-url URL          API base URL (default: https://localhost:7012)
#   --db-container NAME    DB container name (default: auto-detected from docker ps)
#   --expected-volume NAME Expected named volume on the DB container (required)
#   --log-file PATH        API stdout log to scan for validator errors (optional)
#   --help                 Show this help
#
# Examples:
#   ./scripts/post-aspire-restore.sh --expected-volume sentencestudio-local-crispy-barnacle-db-data
#   ./scripts/post-aspire-restore.sh --expected-volume my-vol --api-url https://localhost:7012 --log-file /path/to/api_out
# ---------------------------------------------------------------------------

WEBAPP_URL="https://localhost:7071"
API_URL="https://localhost:7012"
DB_CONTAINER=""
EXPECTED_VOLUME=""
LOG_FILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --webapp-url)    WEBAPP_URL="$2"; shift 2 ;;
    --api-url)       API_URL="$2"; shift 2 ;;
    --db-container)  DB_CONTAINER="$2"; shift 2 ;;
    --expected-volume) EXPECTED_VOLUME="$2"; shift 2 ;;
    --log-file)      LOG_FILE="$2"; shift 2 ;;
    --help)
      sed -n '2,/^# ---/p' "$0" | grep '^#' | sed 's/^# \?//'
      exit 0
      ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

if [[ -z "$EXPECTED_VOLUME" ]]; then
  echo "ERROR: --expected-volume is required."
  echo "Run with --help for usage."
  exit 1
fi

# ── Colors & Counters ────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'
pass=0
fail=0

check_pass() { echo -e "  ${GREEN}PASS${NC} - $1"; pass=$((pass + 1)); }
check_fail() { echo -e "  ${RED}FAIL${NC} - $1"; fail=$((fail + 1)); }

# ── Check 1: WebApp responds ─────────────────────────────────────────────────
echo "Checking WebApp at $WEBAPP_URL ..."
http_code=$("${CURL:-curl}" -sk -o /dev/null -w '%{http_code}' --max-time 10 "$WEBAPP_URL" 2>/dev/null || echo "000")
if [[ "$http_code" =~ ^(200|301|302)$ ]]; then
  check_pass "WebApp responded HTTP $http_code"
else
  check_fail "WebApp returned HTTP $http_code (expected 200/301/302)"
fi

# ── Check 2: API /health returns 200 ─────────────────────────────────────────
echo "Checking API health at $API_URL/health ..."
api_code=$("${CURL:-curl}" -sk -o /dev/null -w '%{http_code}' --max-time 10 "$API_URL/health" 2>/dev/null || echo "000")
if [[ "$api_code" == "200" ]]; then
  check_pass "API /health returned 200"
else
  check_fail "API /health returned HTTP $api_code (expected 200)"
fi

# ── Check 3: DB container has expected volume ─────────────────────────────────
echo "Checking DB volume mount ..."
if [[ -z "$DB_CONTAINER" ]]; then
  # Auto-detect: find a running postgres container with the volume name
  DB_CONTAINER=$("${DOCKER:-docker}" ps --filter "ancestor=postgres" --format '{{.Names}}' 2>/dev/null | head -1)
  if [[ -z "$DB_CONTAINER" ]]; then
    DB_CONTAINER=$("${DOCKER:-docker}" ps --format '{{.Names}}' 2>/dev/null | grep -i 'db' | head -1)
  fi
fi

if [[ -z "$DB_CONTAINER" ]]; then
  check_fail "No DB container found (provide --db-container)"
else
  mounted_volumes=$("${DOCKER:-docker}" inspect "$DB_CONTAINER" --format '{{range .Mounts}}{{.Name}} {{end}}' 2>/dev/null || echo "")
  if echo "$mounted_volumes" | grep -q "$EXPECTED_VOLUME"; then
    check_pass "DB container '$DB_CONTAINER' has volume '$EXPECTED_VOLUME'"
  else
    check_fail "DB container '$DB_CONTAINER' does not have volume '$EXPECTED_VOLUME' (has: $mounted_volumes)"
  fi
fi

# ── Check 4: No validator error in log (if provided) ─────────────────────────
if [[ -n "$LOG_FILE" ]]; then
  echo "Scanning log for startup validator errors ..."
  if [[ ! -f "$LOG_FILE" ]]; then
    check_fail "Log file not found: $LOG_FILE"
  else
    dup_count=$(grep -c 'AllowedUserProfileIds\[.\] is a duplicate' "$LOG_FILE" 2>/dev/null || echo "0")
    val_error=$(grep -c 'OptionsValidationException' "$LOG_FILE" 2>/dev/null || echo "0")
    if [[ "$dup_count" -gt 0 || "$val_error" -gt 0 ]]; then
      check_fail "Startup validator error found in log (duplicates=$dup_count, validation_exceptions=$val_error)"
    else
      check_pass "No startup validator errors in log"
    fi
  fi
else
  echo "  (skipping log scan -- no --log-file provided)"
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo ""
echo "Results: $pass passed, $fail failed"
if [[ $fail -gt 0 ]]; then
  exit 1
fi
exit 0
