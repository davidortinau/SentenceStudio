#!/usr/bin/env bash
# shellcheck disable=SC2016,SC1091,SC2034
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

# Source only the validator's functions. The main routine is guarded by BASH_SOURCE.
# shellcheck source=validate-mobile-migrations.sh
source "$TARGET"
set +e
initialize_physical_temp_root
TEMP_TEST_ROOT=$(mktemp -d "$PHYSICAL_TMP_ROOT/ss-migration-tests-XXXXXX")
chmod 700 "$TEMP_TEST_ROOT"

cleanup() {
    rm -rf "$WORK_DIR" "$TEMP_TEST_ROOT"
    rmdir "$WORK_ROOT" 2>/dev/null || true
}
trap cleanup EXIT

run_log_case() {
    bash "$TARGET" --validate-logs-only "$WORK_DIR/console" "$WORK_DIR/devflow" \
        >/dev/null 2>&1
}

write_logs() {
    printf '%s\n' "$1" > "$WORK_DIR/console"
    printf '%s\n' "${2:-}" > "$WORK_DIR/devflow"
}

assert_source_contains() {
    local pattern="$1"
    local description="$2"
    if grep -Fq -- "$pattern" "$TARGET"; then
        ok "$description"
    else
        bad "$description"
    fi
}

copy_database() {
    local source_database="$1"
    local destination_database="$2"
    cp "$source_database" "$destination_database"
    chmod 600 "$destination_database"
}

rebuild_event_table() {
    local database="$1"
    local omit="$2"
    local sequence_check='CONSTRAINT "CK_ApplicationOperationEvent_Sequence" CHECK ("Sequence" > 0 AND "ApplicationVersion" > 0 AND "Fence" >= 0),'
    local foreign_key='CONSTRAINT "FK_ApplicationOperationEvent_ApplicationOperation_OperationId" FOREIGN KEY ("OperationId") REFERENCES "ApplicationOperation" ("Id") ON DELETE CASCADE'

    [[ "$omit" == "check" ]] && sequence_check=""
    [[ "$omit" == "foreign-key" ]] && foreign_key=""

    sqlite3 "$database" <<SQL
PRAGMA foreign_keys=OFF;
BEGIN;
ALTER TABLE "ApplicationOperationEvent" RENAME TO "ApplicationOperationEvent_Old";
CREATE TABLE "ApplicationOperationEvent" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ApplicationOperationEvent" PRIMARY KEY,
    "OperationId" TEXT NOT NULL,
    "UserProfileId" TEXT NOT NULL,
    "Sequence" INTEGER NOT NULL,
    "Kind" TEXT NOT NULL,
    "FromStatus" TEXT NULL,
    "ToStatus" TEXT NOT NULL,
    "FailureCode" TEXT NULL,
    "ApplicationVersion" INTEGER NOT NULL,
    "Fence" INTEGER NOT NULL,
    "OccurredAtUtc" TEXT NOT NULL,
    CONSTRAINT "CK_ApplicationOperationEvent_Failure" CHECK (("Kind" = 'Failed' AND "FailureCode" IN ('InvalidProtectedContent','InvalidCanonicalRequest','CapabilityUnavailable','AuthorizationDenied','StaleDomainVersion','StaleSynchronizationVersion','PreEffectHandlerFailure')) OR ("Kind" <> 'Failed' AND "FailureCode" IS NULL)),
    CONSTRAINT "CK_ApplicationOperationEvent_Kind" CHECK ("Kind" IN ('Proposed','AwaitingProtectedConfirmation','ExecutionClaimed','LeaseRecovered','Executed','Rejected','Cancelled','Expired','Failed','ReversalLinked','Reversed','ContinuationCreated','ContinuationResumed')),
    $sequence_check
    CONSTRAINT "CK_ApplicationOperationEvent_Status" CHECK (("FromStatus" IS NULL OR "FromStatus" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed')) AND "ToStatus" IN ('Proposed','AwaitingProtectedConfirmation','Executing','Executed','Rejected','Cancelled','Expired','Failed','Reversed'))
    ${foreign_key:+,$foreign_key}
);
INSERT INTO "ApplicationOperationEvent" SELECT * FROM "ApplicationOperationEvent_Old";
DROP TABLE "ApplicationOperationEvent_Old";
CREATE UNIQUE INDEX "IX_ApplicationOperationEvent_OperationId_Sequence"
    ON "ApplicationOperationEvent" ("OperationId", "Sequence");
CREATE INDEX "IX_ApplicationOperationEvent_UserProfileId_OccurredAtUtc"
    ON "ApplicationOperationEvent" ("UserProfileId", "OccurredAtUtc");
COMMIT;
PRAGMA foreign_keys=ON;
SQL
}

replace_confirmation_partial_index_filter() {
    local database="$1"
    local filter="$2"

    sqlite3 "$database" \
        "DROP INDEX \"IX_ApplicationOperationConfirmation_OperationId\";
         CREATE UNIQUE INDEX \"IX_ApplicationOperationConfirmation_OperationId\"
             ON \"ApplicationOperationConfirmation\" (\"OperationId\")
             WHERE $filter;"
}

assert_filter_case_fails() {
    local name="$1"
    local filter="$2"
    local description="$3"
    local database="$SCHEMA_ROOT/database/$name.db3"

    copy_database "$FIXTURE_DB" "$database"
    replace_confirmation_partial_index_filter "$database" "$filter"
    if bash "$TARGET" --validate-database-only "$database" "$MIGRATION_ID" \
        >/dev/null 2>&1; then
        bad "$description"
    else
        ok "$description"
    fi
}

echo "validate-mobile-migrations.test.sh"

write_logs \
"info: Starting migration
info: Mobile schema sanity check PASSED - 8 tables, 13 columns verified
fail: CoreSync update failed: SQLite Error 19: UNIQUE constraint failed" \
"fail: CoreSync update failed: SQLite Error 19: UNIQUE constraint failed"
if run_log_case; then
    ok "runtime SQLite errors after sanity do not fail either log source"
else
    bad "runtime SQLite errors after sanity should be outside migration scope"
fi

write_logs \
"info: Starting migration
fail: SQLite Error 1: no such table: ActivitySession
info: Mobile schema sanity check PASSED - 8 tables, 13 columns verified"
if run_log_case; then
    bad "SQLite error before sanity signal must fail"
else
    ok "SQLite error before sanity signal fails closed"
fi

fatal_startup_messages=(
    "FATAL: Database migration failed. App cannot continue with stale schema."
    "FATAL: Database migration failed on server."
    "FATAL: SyncService initialization failed completely: simulated startup failure"
    "FATAL ERROR in database initialization"
    "Mobile schema sanity check FAILED - missing table"
)

for fatal_message in "${fatal_startup_messages[@]}"; do
    write_logs \
"info: Starting migration
info: Mobile schema sanity check PASSED - 8 tables, 13 columns verified
crit: $fatal_message"
    if run_log_case; then
        bad "fatal message must fail after sanity: $fatal_message"
    else
        ok "fatal message fails after sanity: $fatal_message"
    fi
done

write_logs \
"info: Starting migration
info: Mobile schema sanity check PASSED - 8 tables, 13 columns verified" \
"crit: FATAL ERROR in database initialization"
if run_log_case; then
    bad "fatal message in supplementary log must fail"
else
    ok "fatal message in supplementary log fails"
fi

printf '%s\n' \
    "info: Starting migration" \
    "info: Mobile schema sanity check PASSED - 8 tables, 13 columns verified" \
    > "$WORK_DIR/console"
printf '%s\n' \
    '[' \
    '  {"m":"Mobile schema sanity check PASSED — 8 tables, 13 columns verified"},' \
    '  {"m":"Migration validation opened database binding: main=/tmp/current.db3 device=1 inode=2 fd=9"},' \
    '  {"m":"FATAL ERROR in database initialization"}' \
    ']' > "$WORK_DIR/devflow-raw"
if filter_devflow_log_for_validation_run \
    "$WORK_DIR/devflow-raw" \
    "$WORK_DIR/devflow" \
    "Migration validation opened database binding: main=/tmp/current.db3 device=1 inode=2 fd=" &&
    run_log_case &&
    ! grep -Fq "FATAL ERROR" "$WORK_DIR/devflow"; then
    ok "DevFlow validation ignores entries older than the exact opened-binding boundary"
else
    bad "stale DevFlow failures from a prior launch must not contaminate the current run"
fi

write_logs "info: Starting migration without completing sanity"
if run_log_case; then
    bad "missing sanity signal must fail"
else
    ok "missing sanity signal fails closed"
fi

: > "$WORK_DIR/console"
printf '%s\n' "info: Mobile schema sanity check PASSED" > "$WORK_DIR/devflow"
if run_log_case; then
    bad "empty primary console log must fail"
else
    ok "empty primary console log fails closed"
fi

MIGRATION_ID=$(discover_latest_migration_id)
SCHEMA_ROOT=$(mktemp -d "$TEMP_TEST_ROOT/schema-XXXXXX")
chmod 700 "$SCHEMA_ROOT"
mkdir -m 700 "$SCHEMA_ROOT/database"
FIXTURE_DB="$SCHEMA_ROOT/database/complete.db3"
LOG_PREFIX="$SCHEMA_ROOT/schema-fixture"
create_private_file_exclusive "$FIXTURE_DB"
if prepare_database_to_migration "$FIXTURE_DB" "$MIGRATION_ID"; then
    if bash "$TARGET" --validate-database-only "$FIXTURE_DB" "$MIGRATION_ID" \
        >/dev/null 2>&1; then
        ok "direct validation accepts the real complete EF migration schema"
    else
        bad "direct validation should accept the real complete EF migration schema"
    fi
else
    bad "EF tooling should create the complete schema fixture"
fi

TOY_DB="$SCHEMA_ROOT/database/id-only.db3"
sqlite3 "$TOY_DB" \
    "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);
     INSERT INTO \"__EFMigrationsHistory\" VALUES ('$MIGRATION_ID', 'test');"
for table in \
    ApplicationOperation \
    ApplicationOperationConfirmation \
    ApplicationOperationContinuation \
    ApplicationOperationEvent \
    ApplicationOperationReceipt \
    ApplicationProtectedPayload; do
    sqlite3 "$TOY_DB" "CREATE TABLE \"$table\" (\"Id\" TEXT NOT NULL PRIMARY KEY);"
done
sqlite3 "$TOY_DB" \
    'CREATE INDEX "IX_ApplicationOperation_ParentOperationId" ON "ApplicationOperation" ("Id");
     CREATE INDEX "IX_ApplicationOperationConfirmation_OperationId_ConfirmationDigest" ON "ApplicationOperationConfirmation" ("Id");
     CREATE INDEX "IX_ApplicationOperationContinuation_UserProfileId_InteractionScopeDigest" ON "ApplicationOperationContinuation" ("Id");
     CREATE INDEX "IX_ApplicationOperationEvent_OperationId_Sequence" ON "ApplicationOperationEvent" ("Id");
     CREATE INDEX "IX_ApplicationOperationReceipt_OperationId" ON "ApplicationOperationReceipt" ("Id");
     CREATE INDEX "IX_ApplicationProtectedPayload_SubjectKind_SubjectId_ContentKind" ON "ApplicationProtectedPayload" ("Id");'
if bash "$TARGET" --validate-database-only "$TOY_DB" "$MIGRATION_ID" >/dev/null 2>&1; then
    bad "Id-only toy tables and indexes must fail the complete schema contract"
else
    ok "Id-only toy tables and indexes fail the complete schema contract"
fi

WRONG_INDEX_DB="$SCHEMA_ROOT/database/wrong-index.db3"
copy_database "$FIXTURE_DB" "$WRONG_INDEX_DB"
sqlite3 "$WRONG_INDEX_DB" \
    'DROP INDEX "IX_ApplicationOperationEvent_OperationId_Sequence";
     CREATE UNIQUE INDEX "IX_ApplicationOperationEvent_OperationId_Sequence"
         ON "ApplicationOperationEvent" ("Id");'
if bash "$TARGET" --validate-database-only "$WRONG_INDEX_DB" "$MIGRATION_ID" \
    >/dev/null 2>&1; then
    bad "named index with wrong columns must fail"
else
    ok "named index with wrong columns fails"
fi

EXPECTED_CONFIRMATION_FILTER='"ConsumedAtUtc" IS NULL AND "RevokedAtUtc" IS NULL'
assert_filter_case_fails \
    "filter-and-zero" \
    "$EXPECTED_CONFIRMATION_FILTER AND 0" \
    "partial index expected clause plus AND 0 fails exact filter comparison"
assert_filter_case_fails \
    "filter-or-one" \
    "$EXPECTED_CONFIRMATION_FILTER OR 1" \
    "partial index expected clause plus OR 1 fails exact filter comparison"
assert_filter_case_fails \
    "filter-reordered" \
    '"RevokedAtUtc" IS NULL AND "ConsumedAtUtc" IS NULL' \
    "partial index reordered terms fail the canonical filter contract"
assert_filter_case_fails \
    "filter-missing-term" \
    '"ConsumedAtUtc" IS NULL' \
    "partial index missing term fails the canonical filter contract"

HARMLESS_FILTER_DB="$SCHEMA_ROOT/database/filter-harmless-normalization.db3"
copy_database "$FIXTURE_DB" "$HARMLESS_FILTER_DB"
replace_confirmation_partial_index_filter "$HARMLESS_FILTER_DB" \
    $'consumedatutc is null\n    AnD "REVOKEDATUTC" Is NuLl'
if bash "$TARGET" --validate-database-only "$HARMLESS_FILTER_DB" "$MIGRATION_ID" \
    >/dev/null 2>&1; then
    ok "partial index normalization accepts only whitespace, identifier quoting, and case"
else
    bad "harmless partial-index whitespace, quoting, and case should pass"
fi

MISSING_FK_DB="$SCHEMA_ROOT/database/missing-fk.db3"
copy_database "$FIXTURE_DB" "$MISSING_FK_DB"
rebuild_event_table "$MISSING_FK_DB" "foreign-key"
if bash "$TARGET" --validate-database-only "$MISSING_FK_DB" "$MIGRATION_ID" \
    >/dev/null 2>&1; then
    bad "schema missing a required foreign key must fail"
else
    ok "schema missing a required foreign key fails"
fi

MISSING_CHECK_DB="$SCHEMA_ROOT/database/missing-check.db3"
copy_database "$FIXTURE_DB" "$MISSING_CHECK_DB"
rebuild_event_table "$MISSING_CHECK_DB" "check"
if bash "$TARGET" --validate-database-only "$MISSING_CHECK_DB" "$MIGRATION_ID" \
    >/dev/null 2>&1; then
    bad "schema missing a required check constraint must fail"
else
    ok "schema missing a required check constraint fails"
fi

ISOLATION_ROOT=$(mktemp -d "$TEMP_TEST_ROOT/isolation-XXXXXX")
chmod 700 "$ISOLATION_ROOT"
mkdir -m 700 "$ISOLATION_ROOT/database"
ISOLATION_DB="$ISOLATION_ROOT/database/disposable.db3"
(set -o noclobber; : > "$ISOLATION_DB")
chmod 600 "$ISOLATION_DB"
ISOLATION_LIVE_DIR=$(mktemp -d "$TEMP_TEST_ROOT/live-XXXXXX")
chmod 700 "$ISOLATION_LIVE_DIR"
ISOLATION_LIVE="$ISOLATION_LIVE_DIR/sstudio.db3"

if validate_isolation_paths "$ISOLATION_ROOT" "$ISOLATION_DB" "$ISOLATION_LIVE" \
    >/dev/null 2>&1; then
    ok "canonical current-user private temp root and unique file are accepted"
else
    bad "canonical private temp isolation should be accepted"
fi

NON_TEMP_ROOT="$WORK_DIR/non-temp-root"
mkdir -m 700 "$NON_TEMP_ROOT"
mkdir -m 700 "$NON_TEMP_ROOT/database"
: > "$NON_TEMP_ROOT/database/disposable.db3"
chmod 600 "$NON_TEMP_ROOT/database/disposable.db3"
if validate_isolation_paths \
    "$NON_TEMP_ROOT" "$NON_TEMP_ROOT/database/disposable.db3" "$ISOLATION_LIVE" \
    >/dev/null 2>&1; then
    bad "non-temp validation root must fail"
else
    ok "non-temp validation root fails"
fi

SYMLINK_PARENT="$TEMP_TEST_ROOT/symlink-physical"
mkdir -m 700 "$SYMLINK_PARENT"
mkdir -m 700 "$SYMLINK_PARENT/validator"
mkdir -m 700 "$SYMLINK_PARENT/validator/database"
: > "$SYMLINK_PARENT/validator/database/disposable.db3"
chmod 600 "$SYMLINK_PARENT/validator/database/disposable.db3"
ln -s "$SYMLINK_PARENT" "$TEMP_TEST_ROOT/symlink-alias"
if validate_isolation_paths \
    "$TEMP_TEST_ROOT/symlink-alias/validator" \
    "$TEMP_TEST_ROOT/symlink-alias/validator/database/disposable.db3" \
    "$ISOLATION_LIVE" >/dev/null 2>&1; then
    bad "symlinked validation ancestor must fail"
else
    ok "symlinked validation ancestor fails before physical resolution"
fi

ln "$ISOLATION_DB" "$ISOLATION_ROOT/database/unrelated-hardlink.db3"
if validate_isolation_paths "$ISOLATION_ROOT" "$ISOLATION_DB" "$ISOLATION_LIVE" \
    >/dev/null 2>&1; then
    bad "hardlinked disposable database must fail"
else
    ok "hardlinked disposable database fails link-count validation"
fi
rm "$ISOLATION_ROOT/database/unrelated-hardlink.db3"

ln "$ISOLATION_DB" "$ISOLATION_LIVE"
identity_error=$(validate_isolation_paths \
    "$ISOLATION_ROOT" "$ISOLATION_DB" "$ISOLATION_LIVE" 2>&1)
if [[ $? -ne 0 && "$identity_error" == *"shares device and inode"* ]]; then
    ok "live database device/inode alias fails before link-count fallback"
else
    bad "live database device/inode alias must fail explicitly"
fi

cleanup_output=$(
    MIGRATION_VALIDATION_TEST_HOOK=1 \
        bash "$TARGET" --test-early-timeout-cleanup 2>&1
)
cleanup_status=$?
cleanup_pid=$(printf '%s\n' "$cleanup_output" |
    sed -n 's/^TEST_HELPER_PID=//p' |
    head -1)
if [[ "$cleanup_status" -eq 124 &&
      "$cleanup_pid" =~ ^[0-9]+$ ]] &&
    ! kill -0 "$cleanup_pid" 2>/dev/null; then
    ok "exit trap terminates the exact process on an early DevFlow timeout"
else
    bad "early timeout must not leak the validation process"
    if [[ "$cleanup_pid" =~ ^[0-9]+$ ]]; then
        kill "$cleanup_pid" 2>/dev/null || true
    fi
fi

pid_gap_output=$(
    MIGRATION_VALIDATION_TEST_HOOK=1 \
        bash "$TARGET" --test-pid-gap-cleanup 2>&1
)
pid_gap_status=$?
pid_gap_preexisting=$(printf '%s\n' "$pid_gap_output" |
    sed -n 's/^TEST_PREEXISTING_PID=//p' |
    head -1)
pid_gap_launched=$(printf '%s\n' "$pid_gap_output" |
    sed -n 's/^TEST_LAUNCHED_PID=//p' |
    head -1)
for _ in $(seq 1 50); do
    if [[ ! "$pid_gap_launched" =~ ^[0-9]+$ ]] ||
       ! kill -0 "$pid_gap_launched" 2>/dev/null; then
        break
    fi
    sleep 0.02
done
if [[ "$pid_gap_status" -eq 124 &&
      "$pid_gap_preexisting" =~ ^[0-9]+$ &&
      "$pid_gap_launched" =~ ^[0-9]+$ ]] &&
    kill -0 "$pid_gap_preexisting" 2>/dev/null &&
    ! kill -0 "$pid_gap_launched" 2>/dev/null; then
    ok "PID-gap exit trap terminates only the post-snapshot exact-token process"
else
    bad "PID-gap cleanup must stop the launched process and preserve the preexisting process"
fi
if [[ "$pid_gap_preexisting" =~ ^[0-9]+$ ]]; then
    kill "$pid_gap_preexisting" 2>/dev/null || true
fi
if [[ "$pid_gap_launched" =~ ^[0-9]+$ ]]; then
    kill "$pid_gap_launched" 2>/dev/null || true
fi

assert_source_contains \
    'prepare_database_to_migration "$disposable_db" "$previous_migration"' \
    "full validator prepares a fresh immediately-previous EF migration state"
assert_source_contains \
    'validate_pending_precondition "$disposable_db" "$latest_migration" "$previous_migration"' \
    "full validator requires latest migration count zero before launch"
assert_source_contains \
    '"--migration-validation-db-device" "$database_device"' \
    "launch contract binds the prepared filesystem device"
assert_source_contains \
    '"--migration-validation-db-inode" "$database_inode"' \
    "launch contract binds the prepared filesystem inode"
assert_source_contains \
    '"--migration-validation-launch-token" "$launch_token"' \
    "launch contract carries a unique high-entropy process token"
assert_source_contains \
    'resolve_new_app_pid "$APP_BINARY" "$launch_token" "$pids_before"' \
    "PID resolution requires the exact executable and launch token"
assert_source_contains \
    'resolve_unassigned_app_pid_for_cleanup' \
    "exit cleanup resolves a process while APP_PID is still unset"
assert_source_contains \
    'verify_process_port_association "$APP_PID" "$devflow_port"' \
    "DevFlow listener must belong to the captured app PID"
assert_source_contains \
    'validate_disposable_database "$disposable_db" "$latest_migration"' \
    "full validator requires the complete direct schema contract"
assert_source_contains \
    'verify_source_unchanged "$source_before" "$source_after"' \
    "full validator proves stable live read-only fingerprints are unchanged"
assert_source_contains \
    'if [[ -n "$open_files" ]]' \
    "active live database prevents unstable comparison"
assert_source_contains \
    'kill -KILL "$pid"' \
    "cleanup escalation is exact-PID only"
assert_source_contains \
    '--migration-validation-build-connection-string' \
    "EF fixture connection strings come from the Debug SqliteConnectionStringBuilder helper"
assert_source_contains \
    'NETSDK1112' \
    "missing runtime packs trigger the approved mirror restore fallback"

if grep -Fq -- '--connection "Data Source=$database"' "$TARGET"; then
    bad "validator must not interpolate a database path into an EF connection string"
else
    ok "validator contains no raw database-path connection-string interpolation"
fi

if grep -Eq '(^|[[:space:]])(pkill|killall)([[:space:]]|$)' "$TARGET"; then
    bad "validator must never use broad process termination"
else
    ok "validator contains no broad process termination"
fi

if grep -Fq '.backup' "$TARGET" || grep -Fq 'Creating a consistent disposable upgrade copy' "$TARGET"; then
    bad "validator must not use the live database as migration input"
else
    ok "validator never copies the live database into the pending fixture"
fi

capture_pid_line=$(grep -n '^    resolve_new_app_pid "$APP_BINARY" "$launch_token"' "$TARGET" | cut -d: -f1)
devflow_wait_line=$(grep -n 'Waiting for the DevFlow agent' "$TARGET" | cut -d: -f1)
if [[ "$capture_pid_line" =~ ^[0-9]+$ &&
      "$devflow_wait_line" =~ ^[0-9]+$ &&
      "$capture_pid_line" -lt "$devflow_wait_line" ]]; then
    ok "validator captures exact app PID before any DevFlow wait"
else
    bad "app PID must be captured before early timeout paths"
fi

pending_line=$(grep -n 'validate_pending_precondition "$disposable_db"' "$TARGET" | cut -d: -f1)
launch_line=$(grep -n '^    open -n "$APP_BUNDLE"' "$TARGET" | cut -d: -f1)
post_line=$(grep -n 'validate_disposable_database "$disposable_db"' "$TARGET" | cut -d: -f1)
if [[ "$pending_line" =~ ^[0-9]+$ &&
      "$launch_line" =~ ^[0-9]+$ &&
      "$post_line" =~ ^[0-9]+$ &&
      "$pending_line" -lt "$launch_line" &&
      "$launch_line" -lt "$post_line" ]]; then
    ok "pending-state proof precedes launch and applied-state proof follows it"
else
    bad "validator must prove the exact 0-to-1 migration transition"
fi

echo
echo "passed: $pass   failed: $fail"
[[ "$fail" -eq 0 ]]
