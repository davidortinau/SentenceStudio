#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TARGET="$SCRIPT_DIR/validate-mobile-migrations.sh"
WORK_ROOT="$SCRIPT_DIR/.test-work"
WORK_DIR="$WORK_ROOT/validate-mobile-migrations.$$"

pass=0
fail=0

ok()  { echo "  ok   - $1"; pass=$((pass + 1)); }
bad() { echo "  FAIL - $1"; fail=$((fail + 1)); }

mkdir -p "$WORK_DIR"
trap 'rm -rf "$WORK_DIR"; rmdir "$WORK_ROOT" 2>/dev/null || true' EXIT

run_case() {
    bash "$TARGET" --validate-logs-only "$WORK_DIR/console" "$WORK_DIR/devflow" \
        >/dev/null 2>&1
}

write_logs() {
    printf '%s\n' "$1" > "$WORK_DIR/console"
    printf '%s\n' "${2:-}" > "$WORK_DIR/devflow"
}

echo "validate-mobile-migrations.test.sh"

write_logs \
"info: Starting migration
info: Mobile schema sanity check PASSED — 8 tables, 13 columns verified
fail: CoreSync update failed: SQLite Error 19: UNIQUE constraint failed" \
"fail: CoreSync update failed: SQLite Error 19: UNIQUE constraint failed"
if run_case; then
    ok "runtime SQLite errors after sanity signal do not fail either log source"
else
    bad "runtime SQLite errors after sanity signal should be out of migration scope"
fi

write_logs \
"info: Starting migration
fail: SQLite Error 1: no such table: ActivitySession
info: Mobile schema sanity check PASSED — 8 tables, 13 columns verified"
if run_case; then
    bad "SQLite error before sanity signal must fail"
else
    ok "SQLite error before sanity signal fails closed"
fi

fatal_startup_messages=(
    "FATAL: Database migration failed. App cannot continue with stale schema. Uninstall and reinstall may be required, or contact support."
    "FATAL: Database migration failed on server. Check connection string and database state."
    "FATAL: SyncService initialization failed completely: simulated startup failure"
    "FATAL ERROR in database initialization"
    "Mobile schema sanity check FAILED — 1 missing items after migration: Table: ActivitySession"
)

for fatal_message in "${fatal_startup_messages[@]}"; do
    write_logs \
"info: Starting migration
info: Mobile schema sanity check PASSED — 8 tables, 13 columns verified
crit: $fatal_message"
    if run_case; then
        bad "production fatal message must fail after sanity: $fatal_message"
    else
        ok "production fatal message fails after sanity: $fatal_message"
    fi
done

write_logs \
"info: Starting migration
info: Mobile schema sanity check PASSED — 8 tables, 13 columns verified" \
"crit: FATAL ERROR in database initialization"
if run_case; then
    bad "production fatal message in supplementary log must fail"
else
    ok "production fatal message in supplementary log fails"
fi

write_logs \
"info: Starting migration without completing sanity" \
"fail: SQLite Error 19: constraint failed"
if run_case; then
    bad "SQLite error in supplementary log without sanity signal must fail"
else
    ok "missing sanity boundary scans full supplementary log"
fi

write_logs "info: Starting migration without completing sanity"
if run_case; then
    bad "missing sanity signal must fail"
else
    ok "missing sanity signal fails closed"
fi

: > "$WORK_DIR/console"
printf '%s\n' "info: Mobile schema sanity check PASSED" > "$WORK_DIR/devflow"
if run_case; then
    bad "empty primary console log must fail"
else
    ok "empty primary console log fails closed"
fi

echo
echo "passed: $pass   failed: $fail"
[[ "$fail" -eq 0 ]]
