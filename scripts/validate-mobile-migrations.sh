#!/usr/bin/env bash
set -euo pipefail

# Validates the latest SQLite migration through the macOS AppKit head without using live
# data as input. A fresh, mode-600 database is advanced to the immediately previous
# migration with EF tooling, then the native app must apply the one pending migration.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TFM="net11.0-macos"
PROJECT="$REPO_ROOT/src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj"
SHARED_PROJECT="$REPO_ROOT/src/SentenceStudio.Shared/SentenceStudio.Shared.csproj"
APP_LIB_PROJECT="$REPO_ROOT/src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj"
MIGRATIONS_DIR="$REPO_ROOT/src/SentenceStudio.Shared/Migrations/Sqlite"
EXPECTED_SCHEMA_MIGRATION="20260903175044_AddApplicationOperationLedger"
WAIT_TIMEOUT="${MIGRATION_VALIDATION_WAIT_TIMEOUT:-90}"
SIGNAL_TIMEOUT="${MIGRATION_VALIDATION_SIGNAL_TIMEOUT:-60}"

MIGRATION_ERROR_PATTERN="FATAL: Database migration failed|FATAL: SyncService initialization failed completely|FATAL ERROR in database initialization|Mobile schema sanity check FAILED|no such column|no such table"
SQLITE_ERROR_PATTERN="SQLite Error"
SANITY_SIGNAL="Mobile schema sanity check PASSED"
VALIDATION_PATH_SIGNAL="Migration validation database:"
OPENED_BINDING_SIGNAL="Migration validation opened database binding:"
SYNC_SUPPRESSION_SIGNAL="Migration validation automatic startup suppressed after database initialization."
FORBIDDEN_STARTUP_PATTERN="\\[CoreSync\\] (Background sync|Connectivity sync|Post-login sync)|Pre-loading auth token cache at startup"

APP_PID=""
APP_BINARY=""
APP_BUNDLE=""
APP_LAUNCH_TOKEN=""
APP_PRELAUNCH_PIDS=""
APP_CLEANUP_TRACKING=false
LOG_DIR=""
LOG_PREFIX=""
PHYSICAL_TMP_ROOT=""

fail() {
    echo "ERROR: $*" >&2
    return 1
}

discover_migration_ids() {
    grep -hE 'Migration\("[0-9]{14}_[^"]+"\)' "$MIGRATIONS_DIR"/*.cs |
        sed -E 's/.*Migration\("([^"]+)"\).*/\1/' |
        LC_ALL=C sort -u
}

discover_latest_migration_id() {
    local migration_id
    migration_id=$(discover_migration_ids | tail -1)
    if [[ -z "$migration_id" ]]; then
        fail "No attributed SQLite migration could be discovered under $MIGRATIONS_DIR."
        return 1
    fi
    printf '%s\n' "$migration_id"
}

discover_previous_migration_id() {
    local migration_id
    migration_id=$(discover_migration_ids | tail -2 | head -1)
    if [[ -z "$migration_id" ]]; then
        fail "The immediately previous attributed SQLite migration could not be discovered."
        return 1
    fi
    printf '%s\n' "$migration_id"
}

sqlite_readonly() {
    local database="$1"
    shift
    sqlite3 -batch -bail -readonly -nofollow "$database" "$@"
}

choose_available_devflow_port() {
    python3 -c '
import socket
with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
'
}

reject_symlink_ancestors() {
    local path="$1"
    local allow_missing_leaf="${2:-false}"
    local relative
    local current="/"
    local component
    local -a components
    local index

    if [[ "$path" != /* ]]; then
        fail "Path must be absolute: $path"
        return 1
    fi

    relative="${path#/}"
    IFS='/' read -r -a components <<< "$relative"
    for index in "${!components[@]}"; do
        component="${components[$index]}"
        [[ -n "$component" ]] || continue
        current="${current%/}/$component"
        if [[ -L "$current" ]]; then
            fail "Path contains a symbolic-link ancestor: $current"
            return 1
        fi
        if [[ ! -e "$current" ]]; then
            if [[ "$allow_missing_leaf" == "true" && "$index" -eq $((${#components[@]} - 1)) ]]; then
                return 0
            fi
            fail "Path component does not exist: $current"
            return 1
        fi
    done
}

canonical_missing_leaf_path() {
    local path="$1"
    local parent
    parent=$(dirname "$path")
    printf '%s/%s\n' "$(realpath "$parent")" "$(basename "$path")"
}

initialize_physical_temp_root() {
    local requested_temp="${TMPDIR:-/tmp}"
    if [[ ! -d "$requested_temp" ]]; then
        fail "TMPDIR is not an existing directory: $requested_temp"
        return 1
    fi
    PHYSICAL_TMP_ROOT=$(realpath "$requested_temp") || return 1
    reject_symlink_ancestors "$PHYSICAL_TMP_ROOT" || return 1
    if [[ "$(stat -f '%u' "$PHYSICAL_TMP_ROOT")" != "$(id -u)" ||
          "$(stat -f '%Lp' "$PHYSICAL_TMP_ROOT")" != "700" ]]; then
        fail "Canonical physical TMPDIR must be current-user owned with mode 700."
        return 1
    fi
}

validate_private_root() {
    local root="$1"
    local physical_root
    local owner
    local mode

    reject_symlink_ancestors "$root" || return 1
    physical_root=$(realpath "$root") || return 1
    if [[ "$root" != "$physical_root" ]]; then
        fail "Validation root must be passed as its canonical physical path."
        return 1
    fi
    if [[ "$physical_root" == "$PHYSICAL_TMP_ROOT" ||
          "$physical_root" != "$PHYSICAL_TMP_ROOT/"* ]]; then
        fail "Validation root must be a child of the canonical physical TMPDIR."
        return 1
    fi

    owner=$(stat -f '%u' "$physical_root")
    mode=$(stat -f '%Lp' "$physical_root")
    if [[ "$owner" != "$(id -u)" || "$mode" != "700" ]]; then
        fail "Validation root must be owned by the current user with mode 700."
        return 1
    fi
}

validate_marker_file() {
    local root="$1"
    local expected_token="$2"
    local marker="$root/.sentencestudio-migration-validation"
    local actual_token

    reject_symlink_ancestors "$marker" || return 1
    if [[ ! -f "$marker" ||
          "$(stat -f '%u' "$marker")" != "$(id -u)" ||
          "$(stat -f '%Lp' "$marker")" != "600" ||
          "$(stat -f '%l' "$marker")" != "1" ]]; then
        fail "Validation marker must be a current-user mode-600 regular file with link count one."
        return 1
    fi

    actual_token=$(tr -d '\r\n' < "$marker")
    if [[ -z "$expected_token" || "$actual_token" != "$expected_token" ]]; then
        fail "Validation marker token does not match."
        return 1
    fi
}

validate_isolation_paths() {
    local root="$1"
    local database="$2"
    local live_database="$3"
    local database_parent
    local physical_database
    local physical_live
    local db_device
    local db_inode
    local db_links
    local live_device
    local live_inode

    validate_private_root "$root" || return 1
    reject_symlink_ancestors "$database" || return 1
    database_parent=$(dirname "$database")
    reject_symlink_ancestors "$database_parent" || return 1

    if [[ "$(realpath "$database_parent")" != "$database_parent" ||
          "$database" != "$root/"* ]]; then
        fail "Disposable database parent must be a physical directory inside the validation root."
        return 1
    fi
    if [[ "$(stat -f '%u' "$database_parent")" != "$(id -u)" ||
          "$(stat -f '%Lp' "$database_parent")" != "700" ]]; then
        fail "Disposable database parent must be current-user owned with mode 700."
        return 1
    fi
    if [[ ! -f "$database" || -L "$database" ]]; then
        fail "Disposable database must be a regular file."
        return 1
    fi

    physical_database=$(realpath "$database")
    if [[ "$database" != "$physical_database" ]]; then
        fail "Disposable database must be passed as its canonical physical path."
        return 1
    fi
    if [[ "$(stat -f '%u' "$database")" != "$(id -u)" ||
          "$(stat -f '%Lp' "$database")" != "600" ]]; then
        fail "Disposable database must be current-user owned with mode 600."
        return 1
    fi

    db_device=$(stat -f '%d' "$database")
    db_inode=$(stat -f '%i' "$database")
    db_links=$(stat -f '%l' "$database")

    reject_symlink_ancestors "$live_database" "true" || return 1
    physical_live=$(canonical_missing_leaf_path "$live_database")
    if [[ -e "$live_database" ]]; then
        physical_live=$(realpath "$live_database")
        live_device=$(stat -f '%d' "$live_database")
        live_inode=$(stat -f '%i' "$live_database")
        if [[ "$db_device" == "$live_device" && "$db_inode" == "$live_inode" ]]; then
            fail "Disposable database shares device and inode with the live database."
            return 1
        fi
    fi
    if [[ "$physical_database" == "$physical_live" ]]; then
        fail "Disposable and live database physical paths must differ."
        return 1
    fi
    if [[ "$db_links" != "1" ]]; then
        fail "Disposable database must have link count one."
        return 1
    fi

    printf '%s %s\n' "$db_device" "$db_inode"
}

create_private_file_exclusive() {
    local path="$1"
    if [[ -e "$path" || -L "$path" ]]; then
        fail "Refusing to reuse an existing disposable database path: $path"
        return 1
    fi
    reject_symlink_ancestors "$path" "true" || return 1
    (set -o noclobber; : > "$path") 2>/dev/null ||
        {
            fail "Could not exclusively create disposable database: $path"
            return 1
        }
    chmod 600 "$path"
}

build_native_app() {
    local mirror_config="${NUGET_MIRROR_CONFIG:-$HOME/.nuget/NuGet/NuGet.Config}"

    if dotnet build "$PROJECT" -f "$TFM" -c Debug \
        -p:ValidateXcodeVersion=false \
        --no-restore > "$LOG_PREFIX.build" 2>&1; then
        return 0
    fi

    if ! grep -qE 'NU1301|NETSDK1004|NETSDK1047|NETSDK1112|project.assets.json.*not found|assets file.*doesn.t have a target' \
        "$LOG_PREFIX.build"; then
        return 1
    fi
    if [[ ! -f "$mirror_config" ]]; then
        fail "Build requires restore, but no existing NuGet mirror config is available."
        return 1
    fi

    echo "Restoring with the existing NuGet mirror configuration."
    dotnet restore "$PROJECT" \
        -p:ValidateXcodeVersion=false \
        --configfile "$mirror_config" > "$LOG_PREFIX.restore" 2>&1 || return 1
    dotnet build "$PROJECT" -f "$TFM" -c Debug \
        -p:ValidateXcodeVersion=false \
        --no-restore > "$LOG_PREFIX.build" 2>&1
}

resolve_debug_app_binary() {
    local candidate
    candidate=$(find "$(dirname "$PROJECT")/bin/Debug/$TFM" \
        -maxdepth 6 -type f -path '*/SentenceStudio.app/Contents/MacOS/SentenceStudio' \
        -print -quit 2>/dev/null)
    if [[ -n "$candidate" && -x "$candidate" ]]; then
        realpath "$candidate"
        return 0
    fi

    build_native_app || return 1
    candidate=$(find "$(dirname "$PROJECT")/bin/Debug/$TFM" \
        -maxdepth 6 -type f -path '*/SentenceStudio.app/Contents/MacOS/SentenceStudio' \
        -print -quit)
    [[ -n "$candidate" && -x "$candidate" ]] ||
        {
            fail "Could not resolve the Debug validation connection-string helper."
            return 1
        }
    realpath "$candidate"
}

build_sqlite_connection_string() {
    local database="$1"
    local helper_binary="$APP_BINARY"
    local connection_string

    if [[ -z "$helper_binary" || ! -x "$helper_binary" ]]; then
        helper_binary=$(resolve_debug_app_binary) || return 1
    fi

    connection_string=$(
        "$helper_binary" \
            --migration-validation-build-connection-string \
            "$database"
    ) || return 1
    if [[ -z "$connection_string" ]]; then
        fail "SqliteConnectionStringBuilder helper returned an empty connection string."
        return 1
    fi
    printf '%s\n' "$connection_string"
}

resolve_ef_tool_assembly() {
    local tool_root="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools/.store/dotnet-ef"
    local assembly
    assembly=$(find "$tool_root" -type f -path '*/any/ef.dll' -print 2>/dev/null |
        LC_ALL=C sort -V |
        tail -1)
    if [[ -z "$assembly" ]]; then
        fail "Could not locate the installed dotnet-ef managed tool assembly."
        return 1
    fi
    printf '%s\n' "$assembly"
}

resolve_ef_design_assembly() {
    local assets="$REPO_ROOT/src/SentenceStudio.Shared/obj/project.assets.json"
    local version
    local assembly
    version=$(python3 -c '
import json
import sys
assets = json.load(open(sys.argv[1], encoding="utf-8"))
versions = [
    key.split("/", 1)[1]
    for key in assets.get("libraries", {})
    if key.lower().startswith("microsoft.entityframeworkcore.design/")
]
if len(versions) != 1:
    raise SystemExit("Expected one resolved Microsoft.EntityFrameworkCore.Design version")
print(versions[0])
' "$assets")
    assembly="$HOME/.nuget/packages/microsoft.entityframeworkcore.design/$version/lib/net10.0/Microsoft.EntityFrameworkCore.Design.dll"
    if [[ ! -f "$assembly" ]]; then
        fail "Resolved EF design assembly is missing: $assembly"
        return 1
    fi
    printf '%s\n' "$assembly"
}

prepare_database_to_migration() {
    local database="$1"
    local target_migration="$2"
    local shared_output="$REPO_ROOT/src/SentenceStudio.Shared/bin/Debug/$TFM"
    local app_lib_output="$REPO_ROOT/src/SentenceStudio.AppLib/bin/Debug/net11.0"
    local ef_tool
    local ef_design
    local connection_string

    {
        echo "Preparing disposable database with EF migrations through $target_migration."
        GenerateDependencyFile=true GenerateRuntimeConfigurationFiles=true \
            dotnet build "$SHARED_PROJECT" -f "$TFM" -c Debug \
                -p:ValidateXcodeVersion=false --no-restore
        GenerateRuntimeConfigurationFiles=true \
            dotnet build "$APP_LIB_PROJECT" -f net11.0 -c Debug --no-restore
    } > "$LOG_PREFIX.prepare-build" 2>&1

    ef_tool=$(resolve_ef_tool_assembly)
    ef_design=$(resolve_ef_design_assembly)
    connection_string=$(build_sqlite_connection_string "$database")
    dotnet exec \
        --depsfile "$shared_output/SentenceStudio.Shared.deps.json" \
        --additionalprobingpath "$HOME/.nuget/packages" \
        --runtimeconfig "$app_lib_output/SentenceStudio.AppLib.runtimeconfig.json" \
        "$ef_tool" \
        database update "$target_migration" \
        --connection "$connection_string" \
        --assembly "$shared_output/SentenceStudio.Shared.dll" \
        --startup-assembly "$shared_output/SentenceStudio.Shared.dll" \
        --project "$SHARED_PROJECT" \
        --startup-project "$SHARED_PROJECT" \
        --project-dir "$REPO_ROOT/src/SentenceStudio.Shared/" \
        --root-namespace SentenceStudio.Shared \
        --language C# \
        --framework "$TFM" \
        --design-assembly "$ef_design" \
        --configuration Debug \
        --nullable \
        --working-dir "$REPO_ROOT" > "$LOG_PREFIX.prepare" 2>&1
}

validate_pending_precondition() {
    local database="$1"
    local latest_migration="$2"
    local previous_migration="$3"
    local latest_count
    local previous_count
    local history_count
    local expected_history_count
    local last_applied
    local ledger_count
    local prior_column_count

    latest_count=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='$latest_migration';")
    previous_count=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='$previous_migration';")
    history_count=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM \"__EFMigrationsHistory\";")
    expected_history_count=$(
        discover_migration_ids |
            awk -v target="$previous_migration" '
                { count++ }
                $0 == target { print count; found=1; exit }
                END { if (!found) exit 1 }
            '
    )
    last_applied=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1;")
    ledger_count=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND (name LIKE 'ApplicationOperation%' OR name='ApplicationProtectedPayload');")
    prior_column_count=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM pragma_table_info('SkillProfile') WHERE name='IsArchived' AND type='INTEGER' AND \"notnull\"=1;")

    if [[ "$latest_count" != "0" ||
          "$previous_count" != "1" ||
          "$history_count" != "$expected_history_count" ||
          "$last_applied" != "$previous_migration" ||
          "$ledger_count" != "0" ||
          "$prior_column_count" != "1" ]]; then
        fail "Disposable database is not exactly at the immediately previous migration."
        return 1
    fi
    echo "Pending migration precondition verified: $latest_migration count transitioned baseline=0; previous=$previous_migration."
}

validate_schema_contract() {
    local database="$1"
    python3 - "$database" <<'PY'
import re
import sqlite3
import sys
import urllib.parse

database = sys.argv[1]
uri = "file:" + urllib.parse.quote(database, safe="/") + "?mode=ro"
connection = sqlite3.connect(uri, uri=True)
connection.execute("PRAGMA query_only=ON")

columns = {
    "ApplicationOperation": [
        ("Id", "TEXT", 1, 1), ("UserProfileId", "TEXT", 1, 0),
        ("Authority", "TEXT", 1, 0), ("CapabilityCode", "TEXT", 1, 0),
        ("CapabilityFamily", "TEXT", 1, 0), ("CapabilityVersion", "INTEGER", 1, 0),
        ("CapabilityFingerprint", "TEXT", 1, 0), ("Effect", "TEXT", 1, 0),
        ("Confirmation", "TEXT", 1, 0), ("Status", "TEXT", 1, 0),
        ("ParentOperationId", "TEXT", 0, 0), ("ParentApplicationVersion", "INTEGER", 0, 0),
        ("ParentFence", "INTEGER", 0, 0), ("IdempotencyDigest", "BLOB", 0, 0),
        ("CanonicalRequestDigest", "BLOB", 1, 0), ("DecisionReferenceDigest", "BLOB", 0, 0),
        ("Decision", "TEXT", 0, 0), ("ExpectedDomainVersion", "INTEGER", 1, 0),
        ("ExpectedSynchronizationVersion", "INTEGER", 1, 0), ("ApplicationVersion", "INTEGER", 1, 0),
        ("Fence", "INTEGER", 1, 0), ("LeaseId", "TEXT", 0, 0),
        ("LeaseExpiresAtUtc", "TEXT", 0, 0), ("AttemptCount", "INTEGER", 1, 0),
        ("CreatedAtUtc", "TEXT", 1, 0), ("UpdatedAtUtc", "TEXT", 1, 0),
        ("ExpiresAtUtc", "TEXT", 1, 0), ("PurgeAfterUtc", "TEXT", 1, 0),
        ("TerminalAtUtc", "TEXT", 0, 0), ("PayloadPurgedAtUtc", "TEXT", 0, 0),
    ],
    "ApplicationOperationConfirmation": [
        ("Id", "TEXT", 1, 1), ("OperationId", "TEXT", 1, 0),
        ("UserProfileId", "TEXT", 1, 0), ("ConfirmationDigest", "BLOB", 1, 0),
        ("DecisionReferenceDigest", "BLOB", 1, 0), ("CreatedAtUtc", "TEXT", 1, 0),
        ("ExpiresAtUtc", "TEXT", 1, 0), ("ConsumedAtUtc", "TEXT", 0, 0),
        ("RevokedAtUtc", "TEXT", 0, 0), ("ConsumedApplicationVersion", "INTEGER", 0, 0),
        ("ConsumedFence", "INTEGER", 0, 0),
    ],
    "ApplicationOperationContinuation": [
        ("Id", "TEXT", 1, 1), ("OperationId", "TEXT", 1, 0),
        ("UserProfileId", "TEXT", 1, 0), ("Workflow", "TEXT", 1, 0),
        ("WorkflowVersion", "INTEGER", 1, 0), ("State", "TEXT", 1, 0),
        ("InteractionScopeDigest", "BLOB", 1, 0), ("ParentContinuationId", "TEXT", 0, 0),
        ("AllowsAutomaticResume", "INTEGER", 1, 0), ("AutomaticResumeCount", "INTEGER", 1, 0),
        ("ApplicationVersion", "INTEGER", 1, 0), ("CreatedAtUtc", "TEXT", 1, 0),
        ("UpdatedAtUtc", "TEXT", 1, 0), ("ExpiresAtUtc", "TEXT", 1, 0),
        ("PurgeAfterUtc", "TEXT", 1, 0), ("ResumedAtUtc", "TEXT", 0, 0),
    ],
    "ApplicationOperationEvent": [
        ("Id", "TEXT", 1, 1), ("OperationId", "TEXT", 1, 0),
        ("UserProfileId", "TEXT", 1, 0), ("Sequence", "INTEGER", 1, 0),
        ("Kind", "TEXT", 1, 0), ("FromStatus", "TEXT", 0, 0),
        ("ToStatus", "TEXT", 1, 0), ("FailureCode", "TEXT", 0, 0),
        ("ApplicationVersion", "INTEGER", 1, 0), ("Fence", "INTEGER", 1, 0),
        ("OccurredAtUtc", "TEXT", 1, 0),
    ],
    "ApplicationOperationReceipt": [
        ("Id", "TEXT", 1, 1), ("OperationId", "TEXT", 1, 0),
        ("UserProfileId", "TEXT", 1, 0), ("ReceiptVersion", "INTEGER", 1, 0),
        ("BeforeDomainVersion", "INTEGER", 1, 0), ("BeforeSynchronizationVersion", "INTEGER", 1, 0),
        ("AfterDomainVersion", "INTEGER", 1, 0), ("AfterSynchronizationVersion", "INTEGER", 1, 0),
        ("Reversal", "TEXT", 1, 0), ("ReversalExpiresAtUtc", "TEXT", 0, 0),
        ("ReversalOperationId", "TEXT", 0, 0), ("CommittedAtUtc", "TEXT", 1, 0),
    ],
    "ApplicationProtectedPayload": [
        ("Id", "TEXT", 1, 1), ("OperationId", "TEXT", 1, 0),
        ("UserProfileId", "TEXT", 1, 0), ("SubjectKind", "TEXT", 1, 0),
        ("SubjectId", "TEXT", 1, 0), ("ContentKind", "TEXT", 1, 0),
        ("ProtectionVersion", "INTEGER", 1, 0), ("SchemaVersion", "INTEGER", 1, 0),
        ("Ciphertext", "BLOB", 1, 0), ("PlaintextLength", "INTEGER", 1, 0),
        ("CreatedAtUtc", "TEXT", 1, 0), ("PurgeAfterUtc", "TEXT", 1, 0),
    ],
}

foreign_keys = {
    "ApplicationOperation": {
        ("ParentOperationId", "Id", "ApplicationOperation", "NO ACTION", "RESTRICT"),
    },
    "ApplicationOperationConfirmation": {
        ("OperationId", "Id", "ApplicationOperation", "NO ACTION", "CASCADE"),
    },
    "ApplicationOperationContinuation": {
        ("OperationId", "Id", "ApplicationOperation", "NO ACTION", "CASCADE"),
        ("ParentContinuationId", "Id", "ApplicationOperationContinuation", "NO ACTION", "RESTRICT"),
    },
    "ApplicationOperationEvent": {
        ("OperationId", "Id", "ApplicationOperation", "NO ACTION", "CASCADE"),
    },
    "ApplicationOperationReceipt": {
        ("OperationId", "Id", "ApplicationOperation", "NO ACTION", "CASCADE"),
        ("ReversalOperationId", "Id", "ApplicationOperation", "NO ACTION", "RESTRICT"),
    },
    "ApplicationProtectedPayload": {
        ("OperationId", "Id", "ApplicationOperation", "NO ACTION", "CASCADE"),
    },
}

checks = {
    "ApplicationOperation": {
        "CK_ApplicationOperation_Authority": '"Authority" IN (\'NativeLocal\',\'Server\')',
        "CK_ApplicationOperation_Capability": '"CapabilityVersion" > 0 AND length("CapabilityCode") > 0 AND length("CapabilityFamily") > 0 AND length("CapabilityFingerprint") = 71',
        "CK_ApplicationOperation_CanonicalRequestDigest": 'length("CanonicalRequestDigest") = 32',
        "CK_ApplicationOperation_Confirmation": '"Confirmation" IN (\'Gesture\',\'Accept\',\'ProtectedConfirmation\')',
        "CK_ApplicationOperation_Decision": '("DecisionReferenceDigest" IS NULL AND "Decision" IS NULL) OR (length("DecisionReferenceDigest") = 32 AND "Decision" IN (\'Accept\',\'Reject\',\'Cancel\',\'Confirm\'))',
        "CK_ApplicationOperation_Effect": '"Effect" IN (\'Write\',\'Launch\',\'Composite\')',
        "CK_ApplicationOperation_IdempotencyDigest": '"IdempotencyDigest" IS NULL OR length("IdempotencyDigest") = 32',
        "CK_ApplicationOperation_Lease": '("Status" = \'Executing\' AND "LeaseId" IS NOT NULL AND "LeaseExpiresAtUtc" IS NOT NULL AND "AttemptCount" > 0) OR ("Status" <> \'Executing\' AND "LeaseId" IS NULL AND "LeaseExpiresAtUtc" IS NULL)',
        "CK_ApplicationOperation_Lifecycle": '"ExpiresAtUtc" > "CreatedAtUtc" AND "PurgeAfterUtc" >= "CreatedAtUtc" AND "UpdatedAtUtc" >= "CreatedAtUtc"',
        "CK_ApplicationOperation_ParentFence": '("ParentOperationId" IS NULL AND "ParentApplicationVersion" IS NULL AND "ParentFence" IS NULL) OR ("ParentOperationId" IS NOT NULL AND "ParentOperationId" <> "Id" AND "ParentApplicationVersion" > 0 AND "ParentFence" >= 0)',
        "CK_ApplicationOperation_PayloadPurge": '"PayloadPurgedAtUtc" IS NULL OR ("TerminalAtUtc" IS NOT NULL AND "PayloadPurgedAtUtc" >= "TerminalAtUtc")',
        "CK_ApplicationOperation_Status": '"Status" IN (\'Proposed\',\'AwaitingProtectedConfirmation\',\'Executing\',\'Executed\',\'Rejected\',\'Cancelled\',\'Expired\',\'Failed\',\'Reversed\')',
        "CK_ApplicationOperation_Terminal": '("Status" IN (\'Executed\',\'Rejected\',\'Cancelled\',\'Expired\',\'Failed\',\'Reversed\') AND "TerminalAtUtc" >= "CreatedAtUtc") OR ("Status" IN (\'Proposed\',\'AwaitingProtectedConfirmation\',\'Executing\') AND "TerminalAtUtc" IS NULL)',
        "CK_ApplicationOperation_Versions": '"ExpectedDomainVersion" >= 0 AND "ExpectedSynchronizationVersion" >= 0 AND "ApplicationVersion" > 0 AND "Fence" >= 0 AND "AttemptCount" >= 0',
    },
    "ApplicationOperationConfirmation": {
        "CK_ApplicationOperationConfirmation_Consumption": '("ConsumedAtUtc" IS NULL AND "ConsumedApplicationVersion" IS NULL AND "ConsumedFence" IS NULL) OR ("ConsumedAtUtc" IS NOT NULL AND "ConsumedApplicationVersion" > 0 AND "ConsumedFence" >= 0)',
        "CK_ApplicationOperationConfirmation_Digests": 'length("ConfirmationDigest") = 32 AND length("DecisionReferenceDigest") = 32',
        "CK_ApplicationOperationConfirmation_Lifecycle": '"ExpiresAtUtc" > "CreatedAtUtc" AND ("ConsumedAtUtc" IS NULL OR ("ConsumedAtUtc" >= "CreatedAtUtc" AND "ConsumedAtUtc" <= "ExpiresAtUtc")) AND ("RevokedAtUtc" IS NULL OR "RevokedAtUtc" >= "CreatedAtUtc")',
    },
    "ApplicationOperationContinuation": {
        "CK_ApplicationOperationContinuation_Digest": 'length("InteractionScopeDigest") = 32',
        "CK_ApplicationOperationContinuation_Lifecycle": '"ExpiresAtUtc" > "CreatedAtUtc" AND "PurgeAfterUtc" >= "ExpiresAtUtc" AND "UpdatedAtUtc" >= "CreatedAtUtc"',
        "CK_ApplicationOperationContinuation_Resume": '"WorkflowVersion" > 0 AND "AutomaticResumeCount" >= 0 AND "AutomaticResumeCount" <= 1 AND "ApplicationVersion" > 0 AND ("AllowsAutomaticResume" = TRUE OR "AutomaticResumeCount" = 0) AND ("ParentContinuationId" IS NULL OR ("AllowsAutomaticResume" = FALSE AND "AutomaticResumeCount" = 0)) AND (("AutomaticResumeCount" = 0 AND "ResumedAtUtc" IS NULL) OR ("AutomaticResumeCount" = 1 AND "ResumedAtUtc" IS NOT NULL))',
        "CK_ApplicationOperationContinuation_State": '"State" IN (\'AwaitingDecision\',\'ReadyToResume\',\'Completed\',\'Expired\',\'Cancelled\')',
        "CK_ApplicationOperationContinuation_Workflow": '"Workflow" IN (\'Clarification\',\'PostReceiptResume\')',
    },
    "ApplicationOperationEvent": {
        "CK_ApplicationOperationEvent_Failure": '("Kind" = \'Failed\' AND "FailureCode" IN (\'InvalidProtectedContent\',\'InvalidCanonicalRequest\',\'CapabilityUnavailable\',\'AuthorizationDenied\',\'StaleDomainVersion\',\'StaleSynchronizationVersion\',\'PreEffectHandlerFailure\')) OR ("Kind" <> \'Failed\' AND "FailureCode" IS NULL)',
        "CK_ApplicationOperationEvent_Kind": '"Kind" IN (\'Proposed\',\'AwaitingProtectedConfirmation\',\'ExecutionClaimed\',\'LeaseRecovered\',\'Executed\',\'Rejected\',\'Cancelled\',\'Expired\',\'Failed\',\'ReversalLinked\',\'Reversed\',\'ContinuationCreated\',\'ContinuationResumed\')',
        "CK_ApplicationOperationEvent_Sequence": '"Sequence" > 0 AND "ApplicationVersion" > 0 AND "Fence" >= 0',
        "CK_ApplicationOperationEvent_Status": '("FromStatus" IS NULL OR "FromStatus" IN (\'Proposed\',\'AwaitingProtectedConfirmation\',\'Executing\',\'Executed\',\'Rejected\',\'Cancelled\',\'Expired\',\'Failed\',\'Reversed\')) AND "ToStatus" IN (\'Proposed\',\'AwaitingProtectedConfirmation\',\'Executing\',\'Executed\',\'Rejected\',\'Cancelled\',\'Expired\',\'Failed\',\'Reversed\')',
    },
    "ApplicationOperationReceipt": {
        "CK_ApplicationOperationReceipt_Reversal": '"Reversal" IN (\'Unavailable\',\'Available\',\'Expired\',\'Completed\')',
        "CK_ApplicationOperationReceipt_ReversalState": '("Reversal" = \'Available\' AND "ReversalExpiresAtUtc" IS NOT NULL) OR ("Reversal" = \'Completed\' AND "ReversalOperationId" IS NOT NULL) OR ("Reversal" IN (\'Unavailable\',\'Expired\') AND "ReversalOperationId" IS NULL)',
        "CK_ApplicationOperationReceipt_Versions": '"ReceiptVersion" > 0 AND "BeforeDomainVersion" >= 0 AND "BeforeSynchronizationVersion" >= 0 AND "AfterDomainVersion" >= "BeforeDomainVersion" AND "AfterSynchronizationVersion" >= "BeforeSynchronizationVersion"',
    },
    "ApplicationProtectedPayload": {
        "CK_ApplicationProtectedPayload_Content": '"ContentKind" IN (\'CanonicalRequest\',\'ProposalPresentation\',\'PriorState\',\'Receipt\',\'ContinuationState\')',
        "CK_ApplicationProtectedPayload_Length": '"PlaintextLength" > 0 AND "PlaintextLength" <= 1048576 AND length("Ciphertext") > 0 AND length("Ciphertext") <= 1114112',
        "CK_ApplicationProtectedPayload_Lifecycle": '"PurgeAfterUtc" >= "CreatedAtUtc"',
        "CK_ApplicationProtectedPayload_Subject": '("SubjectKind" = \'Operation\' AND "SubjectId" = "OperationId") OR "SubjectKind" = \'Continuation\'',
        "CK_ApplicationProtectedPayload_Versions": '"ProtectionVersion" = 1 AND "SchemaVersion" > 0',
    },
}

indexes = {
    "ApplicationOperation": {
        "IX_ApplicationOperation_ParentOperationId": (("ParentOperationId",), 1, '"ParentOperationId" IS NOT NULL'),
        "IX_ApplicationOperation_Status_LeaseExpiresAtUtc": (("Status", "LeaseExpiresAtUtc"), 0, None),
        "IX_ApplicationOperation_UserProfileId_Authority_DecisionReferenceDigest": (("UserProfileId", "Authority", "DecisionReferenceDigest"), 0, '"DecisionReferenceDigest" IS NOT NULL'),
        "IX_ApplicationOperation_UserProfileId_Authority_IdempotencyDigest": (("UserProfileId", "Authority", "IdempotencyDigest"), 1, '"IdempotencyDigest" IS NOT NULL'),
        "IX_ApplicationOperation_UserProfileId_Authority_CapabilityCode_CapabilityVersion_IdempotencyDigest": (("UserProfileId", "Authority", "CapabilityCode", "CapabilityVersion", "IdempotencyDigest"), 1, '"IdempotencyDigest" IS NOT NULL'),
        "IX_ApplicationOperation_UserProfileId_PurgeAfterUtc": (("UserProfileId", "PurgeAfterUtc"), 0, None),
        "IX_ApplicationOperation_UserProfileId_Status_ExpiresAtUtc": (("UserProfileId", "Status", "ExpiresAtUtc"), 0, None),
    },
    "ApplicationOperationConfirmation": {
        "IX_ApplicationOperationConfirmation_OperationId_ConfirmationDigest": (("OperationId", "ConfirmationDigest"), 1, None),
        "IX_ApplicationOperationConfirmation_OperationId": (("OperationId",), 1, '"ConsumedAtUtc" IS NULL AND "RevokedAtUtc" IS NULL'),
        "IX_ApplicationOperationConfirmation_UserProfileId_ExpiresAtUtc": (("UserProfileId", "ExpiresAtUtc"), 0, None),
    },
    "ApplicationOperationContinuation": {
        "IX_ApplicationOperationContinuation_OperationId": (("OperationId",), 0, None),
        "IX_ApplicationOperationContinuation_ParentContinuationId": (("ParentContinuationId",), 0, None),
        "IX_ApplicationOperationContinuation_UserProfileId_ExpiresAtUtc": (("UserProfileId", "ExpiresAtUtc"), 0, None),
        "IX_ApplicationOperationContinuation_UserProfileId_InteractionScopeDigest": (("UserProfileId", "InteractionScopeDigest"), 1, '"State" IN (\'AwaitingDecision\',\'ReadyToResume\')'),
    },
    "ApplicationOperationEvent": {
        "IX_ApplicationOperationEvent_OperationId_Sequence": (("OperationId", "Sequence"), 1, None),
        "IX_ApplicationOperationEvent_UserProfileId_OccurredAtUtc": (("UserProfileId", "OccurredAtUtc"), 0, None),
    },
    "ApplicationOperationReceipt": {
        "IX_ApplicationOperationReceipt_OperationId": (("OperationId",), 1, None),
        "IX_ApplicationOperationReceipt_ReversalOperationId": (("ReversalOperationId",), 1, '"ReversalOperationId" IS NOT NULL'),
        "IX_ApplicationOperationReceipt_UserProfileId_CommittedAtUtc": (("UserProfileId", "CommittedAtUtc"), 0, None),
    },
    "ApplicationProtectedPayload": {
        "IX_ApplicationProtectedPayload_OperationId": (("OperationId",), 0, None),
        "IX_ApplicationProtectedPayload_SubjectKind_SubjectId_ContentKind": (("SubjectKind", "SubjectId", "ContentKind"), 1, None),
        "IX_ApplicationProtectedPayload_UserProfileId_PurgeAfterUtc": (("UserProfileId", "PurgeAfterUtc"), 0, None),
    },
}

def normalize(value):
    return re.sub(r"\s+", "", value or "")

def extract_complete_where_clause(sql):
    sql = sql or ""
    index = 0
    depth = 0
    while index < len(sql):
        character = sql[index]
        if character in ("'", '"', "`"):
            quote = character
            index += 1
            while index < len(sql):
                if sql[index] == quote:
                    if index + 1 < len(sql) and sql[index + 1] == quote:
                        index += 2
                        continue
                    index += 1
                    break
                index += 1
            continue
        if character == "[":
            closing = sql.find("]", index + 1)
            index = len(sql) if closing < 0 else closing + 1
            continue
        if character == "(":
            depth += 1
            index += 1
            continue
        if character == ")":
            depth = max(0, depth - 1)
            index += 1
            continue
        if character.isalpha() or character == "_":
            end = index + 1
            while end < len(sql) and (sql[end].isalnum() or sql[end] == "_"):
                end += 1
            if depth == 0 and sql[index:end].casefold() == "where":
                clause = sql[end:].strip()
                return clause[:-1].rstrip() if clause.endswith(";") else clause
            index = end
            continue
        index += 1
    return None

def canonical_filter_tokens(expression):
    """Normalize whitespace, SQL keyword/identifier case, and identifier quoting only."""
    tokens = []
    expression = expression or ""
    index = 0
    while index < len(expression):
        character = expression[index]
        if character.isspace():
            index += 1
            continue
        if character == "'":
            end = index + 1
            while end < len(expression):
                if expression[end] == "'":
                    if end + 1 < len(expression) and expression[end + 1] == "'":
                        end += 2
                        continue
                    end += 1
                    break
                end += 1
            if end > len(expression) or expression[end - 1:end] != "'":
                raise ValueError("unterminated SQL string literal")
            tokens.append(("string", expression[index:end]))
            index = end
            continue
        if character in ('"', "`", "["):
            closing = "]" if character == "[" else character
            end = index + 1
            value = []
            while end < len(expression):
                if expression[end] == closing:
                    if closing != "]" and end + 1 < len(expression) and expression[end + 1] == closing:
                        value.append(closing)
                        end += 2
                        continue
                    end += 1
                    break
                value.append(expression[end])
                end += 1
            else:
                raise ValueError("unterminated SQL identifier")
            tokens.append(("word", "".join(value).casefold()))
            index = end
            continue
        if character.isalpha() or character == "_":
            end = index + 1
            while end < len(expression) and (expression[end].isalnum() or expression[end] == "_"):
                end += 1
            tokens.append(("word", expression[index:end].casefold()))
            index = end
            continue
        if character.isdigit():
            end = index + 1
            while end < len(expression) and (
                expression[end].isdigit() or expression[end] in (".", "e", "E", "+", "-")
            ):
                end += 1
            tokens.append(("number", expression[index:end]))
            index = end
            continue
        two_character_operator = expression[index:index + 2]
        if two_character_operator in ("<=", ">=", "<>", "!=", "==", "||"):
            tokens.append(("operator", two_character_operator))
            index += 2
            continue
        tokens.append(("symbol", character))
        index += 1
    return tuple(tokens)

errors = []
for table, expected_columns in columns.items():
    actual_columns = [
        (row[1], row[2].upper(), row[3], row[5])
        for row in connection.execute(f'PRAGMA table_info("{table}")')
    ]
    if actual_columns != expected_columns:
        errors.append(f"{table}: column/type/nullability/PK contract mismatch")

    actual_foreign_keys = {
        (row[3], row[4], row[2], row[5].upper(), row[6].upper())
        for row in connection.execute(f'PRAGMA foreign_key_list("{table}")')
    }
    if actual_foreign_keys != foreign_keys[table]:
        errors.append(f"{table}: foreign-key contract mismatch")

    table_row = connection.execute(
        "SELECT sql FROM sqlite_schema WHERE type='table' AND name=?", (table,)
    ).fetchone()
    table_sql = normalize(table_row[0] if table_row else "")
    actual_check_names = set(re.findall(r'CONSTRAINT"([^"]+)"CHECK\(', table_sql))
    if actual_check_names != set(checks[table]) or table_sql.count("CHECK(") != len(checks[table]):
        errors.append(f"{table}: named check-constraint set mismatch")
    for name, expression in checks[table].items():
        fragment = normalize(f'CONSTRAINT "{name}" CHECK ({expression})')
        if fragment not in table_sql:
            errors.append(f"{table}: missing or changed check constraint {name}")

    actual_index_rows = {
        row[1]: row for row in connection.execute(f'PRAGMA index_list("{table}")')
        if not row[1].startswith("sqlite_autoindex")
    }
    if set(actual_index_rows) != set(indexes[table]):
        errors.append(f"{table}: named index set mismatch")
        continue
    for name, (expected_columns_for_index, expected_unique, expected_filter) in indexes[table].items():
        row = actual_index_rows[name]
        actual_columns_for_index = tuple(
            index_row[2]
            for index_row in connection.execute(f'PRAGMA index_info("{name}")')
        )
        expected_partial = 1 if expected_filter else 0
        if (actual_columns_for_index, row[2], row[4]) != (
            expected_columns_for_index, expected_unique, expected_partial
        ):
            errors.append(f"{table}: index contract mismatch for {name}")
        if expected_filter:
            index_sql_row = connection.execute(
                "SELECT sql FROM sqlite_schema WHERE type='index' AND name=?", (name,)
            ).fetchone()
            actual_filter = extract_complete_where_clause(
                index_sql_row[0] if index_sql_row else ""
            )
            try:
                filter_matches = (
                    actual_filter is not None
                    and canonical_filter_tokens(actual_filter)
                    == canonical_filter_tokens(expected_filter)
                )
            except ValueError:
                filter_matches = False
            if not filter_matches:
                errors.append(f"{table}: index filter mismatch for {name}")

connection.close()
if errors:
    raise SystemExit("\n".join(errors))
print("Complete six-table schema contract passed: columns, nullability, PKs, FKs, checks, and indexes.")
PY
}

validate_disposable_database() {
    local database="$1"
    local migration_id="$2"
    local integrity
    local count
    local foreign_key_errors

    if [[ "$migration_id" != "$EXPECTED_SCHEMA_MIGRATION" ]]; then
        fail "Expected-schema contract is pinned to $EXPECTED_SCHEMA_MIGRATION, not $migration_id."
        return 1
    fi
    if [[ ! -f "$database" || -L "$database" ]]; then
        fail "Disposable database was not created as a regular file: $database"
        return 1
    fi

    integrity=$(sqlite_readonly "$database" "PRAGMA query_only=ON; PRAGMA integrity_check;")
    if [[ "$integrity" != "ok" ]]; then
        fail "Disposable database integrity_check did not return ok: $integrity"
        return 1
    fi
    count=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='$migration_id';")
    if [[ "$count" != "1" ]]; then
        fail "Migration $migration_id is not applied exactly once in the disposable database."
        return 1
    fi
    foreign_key_errors=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; PRAGMA foreign_key_check;")
    if [[ -n "$foreign_key_errors" ]]; then
        fail "Disposable database foreign_key_check reported errors."
        return 1
    fi

    validate_schema_contract "$database"
    echo "Direct SQLite validation passed for migration $migration_id."
}

validate_migration_logs() {
    local console_log="$1"
    local devflow_log="${2:-}"
    local sanity_line=""
    local migration_error=false
    local logfile

    if [[ ! -s "$console_log" ]]; then
        fail "Console output is empty; there is no native migration evidence."
        return 1
    fi
    sanity_line=$(grep -n -m1 "$SANITY_SIGNAL" "$console_log" 2>/dev/null |
        cut -d: -f1 || true)
    for logfile in "$console_log" "$devflow_log"; do
        if [[ -n "$logfile" && -s "$logfile" ]] &&
            grep -iE "$MIGRATION_ERROR_PATTERN" "$logfile" 2>/dev/null; then
            migration_error=true
            echo "Migration or schema error found in: $logfile"
        fi
    done
    if [[ -n "$sanity_line" ]]; then
        if sed -n "1,${sanity_line}p" "$console_log" |
            grep -iE "$SQLITE_ERROR_PATTERN"; then
            migration_error=true
            echo "SQLite error found before schema sanity passed: $console_log"
        fi
    else
        for logfile in "$console_log" "$devflow_log"; do
            if [[ -n "$logfile" && -s "$logfile" ]] &&
                grep -iE "$SQLITE_ERROR_PATTERN" "$logfile" 2>/dev/null; then
                migration_error=true
                echo "SQLite error found in log without a sanity boundary: $logfile"
            fi
        done
    fi
    if [[ "$migration_error" == "true" ]]; then
        return 1
    fi
    if [[ -z "$sanity_line" ]]; then
        fail "Positive sanity signal '$SANITY_SIGNAL' was not found."
        return 1
    fi
}

filter_devflow_log_for_validation_run() {
    local input="$1"
    local output="$2"
    local opened_binding_prefix="$3"

    python3 - "$input" "$output" "$opened_binding_prefix" <<'PY'
import json
import sys

input_path, output_path, opened_binding_prefix = sys.argv[1:]
with open(input_path, encoding="utf-8") as stream:
    entries = json.load(stream)
if not isinstance(entries, list):
    raise SystemExit("DevFlow log response must be a JSON array")

boundary_indexes = [
    index
    for index, entry in enumerate(entries)
    if isinstance(entry, dict)
    and str(entry.get("m", "")).startswith(opened_binding_prefix)
]
if len(boundary_indexes) != 1:
    raise SystemExit(
        f"Expected one current-run opened-binding boundary, found {len(boundary_indexes)}"
    )

# DevFlow returns newest entries first. Migration and sanity happen after the opened-binding
# message, so entries through that unique per-run boundary belong to this validation launch.
current_run_entries = entries[: boundary_indexes[0] + 1]
with open(output_path, "w", encoding="utf-8", newline="\n") as stream:
    json.dump(current_run_entries, stream, ensure_ascii=False, indent=2)
    stream.write("\n")
PY
}

live_database_is_idle() {
    local database="$1"
    local -a files=()
    local file
    local open_files

    for file in "$database" "$database-wal" "$database-shm"; do
        [[ -e "$file" ]] && files+=("$file")
    done
    if [[ "${#files[@]}" -eq 0 ]]; then
        return 0
    fi
    open_files=$(lsof -nP -- "${files[@]}" 2>/dev/null || true)
    if [[ -n "$open_files" ]]; then
        fail "Live database is active; stable read-only comparison is impossible."
        return 1
    fi
}

snapshot_file_metadata() {
    local database="$1"
    local file
    for file in "$database" "$database-wal" "$database-shm"; do
        if [[ -e "$file" ]]; then
            stat -f 'file=%N|device=%d|inode=%i|links=%l|owner=%u|mode=%Lp|size=%z|mtime=%m' "$file"
        else
            printf 'file=%s|absent\n' "$file"
        fi
    done
}

capture_readonly_source_state() {
    local database="$1"
    local output="$2"
    local metadata_before
    local metadata_after
    local integrity
    local schema_hash
    local content_hash
    local history_hash
    local history_exists

    if [[ ! -e "$database" ]]; then
        printf '%s\n' "source=absent" > "$output"
        return 0
    fi
    reject_symlink_ancestors "$database" || return 1
    live_database_is_idle "$database" || return 1
    metadata_before=$(snapshot_file_metadata "$database")

    integrity=$(sqlite_readonly "$database" "PRAGMA query_only=ON; PRAGMA integrity_check;")
    [[ "$integrity" == "ok" ]] ||
        {
            fail "Live database did not pass read-only integrity_check."
            return 1
        }
    schema_hash=$(
        sqlite_readonly "$database" \
            "PRAGMA query_only=ON; SELECT type || '|' || name || '|' || tbl_name || '|' || COALESCE(sql, '') FROM sqlite_schema ORDER BY type, name;" |
            shasum -a 256 |
            awk '{print $1}'
    )
    content_hash=$(
        sqlite_readonly "$database" ".dump --data-only" |
            shasum -a 256 |
            awk '{print $1}'
    )
    history_exists=$(sqlite_readonly "$database" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='__EFMigrationsHistory';")
    if [[ "$history_exists" == "1" ]]; then
        history_hash=$(
            sqlite_readonly "$database" \
                "PRAGMA query_only=ON; SELECT \"MigrationId\" || '|' || \"ProductVersion\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\";" |
                shasum -a 256 |
                awk '{print $1}'
        )
    else
        history_hash="absent"
    fi

    live_database_is_idle "$database" || return 1
    metadata_after=$(snapshot_file_metadata "$database")
    if [[ "$metadata_before" != "$metadata_after" ]]; then
        fail "Live database metadata changed during read-only capture."
        return 1
    fi

    {
        printf 'source=present\n'
        printf 'integrity=%s\n' "$integrity"
        printf 'migration_history_sha256=%s\n' "$history_hash"
        printf 'schema_sha256=%s\n' "$schema_hash"
        printf 'content_sha256=%s\n' "$content_hash"
        printf '%s\n' "$metadata_after"
    } > "$output"
}

verify_source_unchanged() {
    local before="$1"
    local after="$2"
    if ! cmp -s "$before" "$after"; then
        echo "Live database state changed during validation:" >&2
        diff -u "$before" "$after" >&2 || true
        return 1
    fi
    echo "Live database read-only fingerprints and metadata are unchanged."
    grep -E '^(migration_history|schema|content)_sha256=' "$after" || true
}

verify_agent_identity() {
    local list_log="$1"
    local expected_project="$2"
    local expected_tfm="$3"
    local expected_port="$4"

    python3 -c '
import json
import os
import sys
text = open(sys.argv[1], encoding="utf-8").read()
start = text.rfind("\n[")
start = start + 1 if start >= 0 else text.find("[")
if start < 0:
    raise SystemExit("DevFlow list did not contain a JSON array")
agents = json.loads(text[start:])
port = int(sys.argv[4])
matches = [agent for agent in agents if int(agent.get("port", -1)) == port]
if len(matches) != 1:
    raise SystemExit(f"Expected one DevFlow agent on port {port}, found {len(matches)}")
agent = matches[0]
expected = os.path.realpath(sys.argv[2])
checks = {
    "appName": "SentenceStudio",
    "tfm": sys.argv[3],
    "platform": "macOS",
    "project": expected,
}
for key, value in checks.items():
    if agent.get(key) != value:
        raise SystemExit(f"Unexpected DevFlow {key}: {agent.get(key)!r}; expected {value!r}")
print("app=SentenceStudio platform=macOS tfm={} port={} project={}".format(
    sys.argv[3], port, expected))
' "$list_log" "$expected_project" "$expected_tfm" "$expected_port"
}

matching_app_pids() {
    local expected_binary="$1"
    local expected_launch_token="$2"
    local pid
    local command
    while read -r pid command; do
        command="${command#"${command%%[![:space:]]*}"}"
        if [[ "$command" == "$expected_binary "* &&
              " $command " == *" --migration-validation-launch-token $expected_launch_token "* ]]; then
            printf '%s\n' "$pid"
        fi
    done < <(ps -ww -axo pid=,command=)
}

new_matching_app_pids() {
    local expected_binary="$1"
    local expected_launch_token="$2"
    local before_pids="$3"
    local pid

    while IFS= read -r pid; do
        [[ -n "$pid" ]] || continue
        if ! grep -Fxq "$pid" <<< "$before_pids"; then
            printf '%s\n' "$pid"
        fi
    done < <(matching_app_pids "$expected_binary" "$expected_launch_token")
}

line_count() {
    awk 'NF { count++ } END { print count + 0 }'
}

resolve_new_app_pid() {
    local expected_binary="$1"
    local expected_launch_token="$2"
    local before_pids="$3"
    local elapsed=0
    local new_pids
    local match_count
    while [[ "$elapsed" -lt 150 ]]; do
        new_pids=$(
            new_matching_app_pids \
                "$expected_binary" "$expected_launch_token" "$before_pids"
        )
        match_count=$(printf '%s\n' "$new_pids" | line_count)
        if [[ "$match_count" -eq 1 ]]; then
            APP_PID="$new_pids"
            return 0
        fi
        if [[ "$match_count" -gt 1 ]]; then
            fail "More than one new validator process matched the exact executable and launch token: ${new_pids//$'\n'/,}."
            return 1
        fi
        sleep 0.1
        elapsed=$((elapsed + 1))
    done
    fail "Could not resolve the exact new validator process after LaunchServices returned."
}

resolve_unassigned_app_pid_for_cleanup() {
    local elapsed=0
    local new_pids
    local match_count

    if [[ "$APP_CLEANUP_TRACKING" != "true" || -n "$APP_PID" ]]; then
        return 0
    fi
    if [[ -z "$APP_BINARY" || -z "$APP_LAUNCH_TOKEN" ]]; then
        fail "Validator cleanup tracking was armed without an executable and launch token."
        return 1
    fi

    while [[ "$elapsed" -lt 50 ]]; do
        new_pids=$(
            new_matching_app_pids \
                "$APP_BINARY" "$APP_LAUNCH_TOKEN" "$APP_PRELAUNCH_PIDS"
        )
        match_count=$(printf '%s\n' "$new_pids" | line_count)
        if [[ "$match_count" -eq 1 ]]; then
            APP_PID="$new_pids"
            echo "Exit trap resolved unassigned validator process $APP_PID by exact executable and launch token." >&2
            return 0
        fi
        if [[ "$match_count" -gt 1 ]]; then
            fail "Exit trap found multiple new processes for the exact validator executable and launch token (${new_pids//$'\n'/,}); refusing non-unique cleanup."
            return 1
        fi
        sleep 0.1
        elapsed=$((elapsed + 1))
    done

    echo "Validator cleanup: zero new processes matched the exact executable and launch token." >&2
    APP_CLEANUP_TRACKING=false
    return 0
}

verify_process_identity() {
    local pid="$1"
    local expected_binary="$2"
    local command
    local text_path
    local resolved_text_path
    local found=false

    [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null || return 1
    command=$(ps -p "$pid" -o command= | sed -E 's/^[[:space:]]+//')
    if [[ "$command" != "$expected_binary" && "$command" != "$expected_binary "* ]]; then
        return 1
    fi
    while IFS= read -r text_path; do
        [[ -n "$text_path" ]] || continue
        resolved_text_path=$(realpath "$text_path" 2>/dev/null || true)
        if [[ "$resolved_text_path" == "$expected_binary" ]]; then
            found=true
            break
        fi
    done < <(lsof -nP -a -p "$pid" -d txt -Fn 2>/dev/null | sed -n 's/^n//p')
    [[ "$found" == "true" ]]
}

verify_process_port_association() {
    local pid="$1"
    local port="$2"
    local listeners
    listeners=$(lsof -nP -a -p "$pid" -iTCP:"$port" -sTCP:LISTEN -t 2>/dev/null |
        LC_ALL=C sort -u)
    [[ "$listeners" == "$pid" ]] ||
        {
            fail "DevFlow port $port is not owned by validator PID $pid."
            return 1
        }
}

stop_app() {
    local pid
    local elapsed
    resolve_unassigned_app_pid_for_cleanup || return 1
    pid="$APP_PID"
    if [[ ! "$pid" =~ ^[0-9]+$ ]]; then
        return 0
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
        APP_PID=""
        APP_CLEANUP_TRACKING=false
        wait "$pid" 2>/dev/null || true
        return 0
    fi
    if ! verify_process_identity "$pid" "$APP_BINARY"; then
        fail "Refusing to terminate PID $pid because its executable identity changed."
        return 1
    fi

    kill -TERM "$pid" 2>/dev/null || true
    for elapsed in $(seq 1 40); do
        if ! kill -0 "$pid" 2>/dev/null; then
            wait "$pid" 2>/dev/null || true
            echo "Stopped validator app process $pid."
            APP_PID=""
            APP_CLEANUP_TRACKING=false
            return 0
        fi
        sleep 0.25
    done

    if ! verify_process_identity "$pid" "$APP_BINARY"; then
        fail "Refusing exact-PID escalation because PID $pid changed identity."
        return 1
    fi
    kill -KILL "$pid" 2>/dev/null || true
    for elapsed in $(seq 1 20); do
        if ! kill -0 "$pid" 2>/dev/null; then
            wait "$pid" 2>/dev/null || true
            echo "Escalated and stopped validator app process $pid."
            APP_PID=""
            APP_CLEANUP_TRACKING=false
            return 0
        fi
        sleep 0.1
    done
    fail "Validator app process $pid did not stop after exact-PID escalation."
}

on_exit() {
    local status=$?
    local cleanup_status=0
    set +e
    stop_app
    cleanup_status=$?
    if [[ "$status" -eq 0 && "$cleanup_status" -ne 0 ]]; then
        status="$cleanup_status"
    fi
    if [[ "$status" -ne 0 && -n "$LOG_DIR" ]]; then
        echo "Validation artifacts retained after failure: $LOG_DIR" >&2
    fi
    trap - EXIT
    exit "$status"
}

run_early_timeout_cleanup_test_hook() {
    if [[ "${MIGRATION_VALIDATION_TEST_HOOK:-}" != "1" ]]; then
        fail "Cleanup test hook requires MIGRATION_VALIDATION_TEST_HOOK=1."
        return 2
    fi
    APP_BINARY=$(realpath "$(command -v sleep)")
    "$APP_BINARY" 60 &
    APP_PID=$!
    printf 'TEST_HELPER_PID=%s\n' "$APP_PID"
    fail "Simulated timeout before DevFlow is available." || return 124
}

run_pid_gap_cleanup_test_hook() {
    local preexisting_pid
    local launched_pid
    local elapsed

    if [[ "${MIGRATION_VALIDATION_TEST_HOOK:-}" != "1" ]]; then
        fail "Cleanup test hook requires MIGRATION_VALIDATION_TEST_HOOK=1."
        return 2
    fi

    APP_BINARY=$(
        python3 -c 'import os, sys; print(os.path.realpath(os.path.join(sys.prefix, "Resources", "Python.app", "Contents", "MacOS", "Python")))'
    )
    [[ -x "$APP_BINARY" ]] ||
        {
            fail "Could not resolve the fake validator executable."
            return 2
        }
    APP_LAUNCH_TOKEN=$(
        python3 -c 'import secrets; print(secrets.token_hex(32))'
    )
    "$APP_BINARY" -c 'import time; time.sleep(60)' \
        --migration-validation-launch-token "$APP_LAUNCH_TOKEN" \
        </dev/null >/dev/null 2>&1 &
    preexisting_pid=$!
    printf 'TEST_PREEXISTING_PID=%s\n' "$preexisting_pid"

    for elapsed in $(seq 1 50); do
        if matching_app_pids "$APP_BINARY" "$APP_LAUNCH_TOKEN" |
            grep -Fxq "$preexisting_pid"; then
            break
        fi
        sleep 0.02
    done

    APP_PRELAUNCH_PIDS=$(matching_app_pids "$APP_BINARY" "$APP_LAUNCH_TOKEN")
    APP_CLEANUP_TRACKING=true
    "$APP_BINARY" -c 'import time; time.sleep(60)' \
        --migration-validation-launch-token "$APP_LAUNCH_TOKEN" \
        </dev/null >/dev/null 2>&1 &
    launched_pid=$!
    printf 'TEST_OPEN_SUCCEEDED=1\n'
    printf 'TEST_LAUNCHED_PID=%s\n' "$launched_pid"

    # Deliberately leave APP_PID empty. The EXIT trap must identify the one post-snapshot
    # process by exact executable and token, while leaving the preexisting match untouched.
    fail "Simulated interruption after open returned but before APP_PID assignment." || return 124
}

run_full_validation() {
    local validation_root
    local database_dir
    local disposable_db
    local marker_file
    local validation_token
    local launch_token
    local live_db
    local source_before
    local source_after
    local latest_migration
    local previous_migration
    local devflow_port
    local database_identity
    local database_device
    local database_inode
    local pids_before
    local agent_connected=false
    local signals_ready=false
    local elapsed
    local process_command
    local agent_identity

    initialize_physical_temp_root
    umask 077
    LOG_DIR=$(mktemp -d "$PHYSICAL_TMP_ROOT/ss-migration-XXXXXX")
    LOG_DIR=$(realpath "$LOG_DIR")
    chmod 700 "$LOG_DIR"
    LOG_PREFIX="$LOG_DIR/migration-validation"
    validation_root="$LOG_DIR"
    database_dir="$validation_root/database"
    disposable_db="$database_dir/sstudio.db3"
    marker_file="$validation_root/.sentencestudio-migration-validation"
    validation_token=$(uuidgen | tr -d '-')
    launch_token=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
    live_db=$(canonical_missing_leaf_path "$HOME/Library/sstudio.db3")
    source_before="$LOG_PREFIX.source-before"
    source_after="$LOG_PREFIX.source-after"
    latest_migration=$(discover_latest_migration_id)
    previous_migration=$(discover_previous_migration_id)
    devflow_port=$(choose_available_devflow_port)

    [[ "$latest_migration" == "$EXPECTED_SCHEMA_MIGRATION" ]] ||
        {
            fail "Expected-schema contract is stale; latest is $latest_migration."
            return 1
        }
    if [[ ! "$devflow_port" =~ ^[0-9]+$ ]] ||
       [[ "$devflow_port" -lt 1024 || "$devflow_port" -gt 65535 ]]; then
        fail "Could not select a safe per-run DevFlow port."
        return 1
    fi

    mkdir -m 700 "$database_dir"
    (set -o noclobber; printf '%s\n' "$validation_token" > "$marker_file")
    chmod 600 "$marker_file"
    validate_private_root "$validation_root"
    validate_marker_file "$validation_root" "$validation_token"

    if lsof -nP -tiTCP:"$devflow_port" -sTCP:LISTEN 2>/dev/null | grep -q .; then
        fail "Port $devflow_port is already in use; refusing a stale DevFlow agent."
        return 1
    fi

    echo "Validation root: $validation_root"
    echo "Migration transition: $previous_migration -> $latest_migration"
    echo "Building $TFM in Debug configuration."
    if ! build_native_app; then
        tail -30 "$LOG_PREFIX.build" || true
        fail "Build failed. Full build log: $LOG_PREFIX.build"
        return 1
    fi

    APP_BUNDLE=$(find "$(dirname "$PROJECT")/bin/Debug/$TFM" \
        -maxdepth 3 -type d -name "SentenceStudio.app" -print -quit)
    [[ -n "$APP_BUNDLE" ]] ||
        {
            fail "No SentenceStudio.app bundle was found for $TFM."
            return 1
        }
    APP_BUNDLE=$(realpath "$APP_BUNDLE")
    APP_BINARY=$(realpath "$APP_BUNDLE/Contents/MacOS/SentenceStudio")
    [[ -x "$APP_BINARY" ]] ||
        {
            fail "App binary is not executable: $APP_BINARY"
            return 1
        }

    echo "Capturing stable read-only live database state."
    capture_readonly_source_state "$live_db" "$source_before"

    create_private_file_exclusive "$disposable_db"
    prepare_database_to_migration "$disposable_db" "$previous_migration"
    database_identity=$(validate_isolation_paths "$validation_root" "$disposable_db" "$live_db")
    read -r database_device database_inode <<< "$database_identity"
    validate_marker_file "$validation_root" "$validation_token"
    validate_pending_precondition "$disposable_db" "$latest_migration" "$previous_migration"

    local -a launch_arguments=(
        "--migration-validation"
        "--migration-validation-root" "$validation_root"
        "--migration-validation-db" "$disposable_db"
        "--migration-validation-db-device" "$database_device"
        "--migration-validation-db-inode" "$database_inode"
        "--migration-validation-token" "$validation_token"
        "--migration-validation-launch-token" "$launch_token"
        "--migration-validation-devflow-port" "$devflow_port"
        "--migration-validation-disable-sync"
    )

    {
        printf 'bundle=%q\n' "$APP_BUNDLE"
        printf 'executable=%q\n' "$APP_BINARY"
        printf 'launch-token=%q\n' "$launch_token"
        printf 'open -n %q --stdout %q --stderr %q --env %q --args' \
            "$APP_BUNDLE" "$LOG_PREFIX.console" "$LOG_PREFIX.console" \
            "SS_DEVFLOW_TRACE=$LOG_PREFIX.devflow-trace"
        printf ' %q' "${launch_arguments[@]}"
        printf '\n'
    } > "$LOG_PREFIX.launch"

    : > "$LOG_PREFIX.console"
    pids_before=$(matching_app_pids "$APP_BINARY" "$launch_token")
    APP_LAUNCH_TOKEN="$launch_token"
    APP_PRELAUNCH_PIDS="$pids_before"
    APP_CLEANUP_TRACKING=true
    echo "Launching the isolated validator app through LaunchServices."
    open -n "$APP_BUNDLE" \
        --stdout "$LOG_PREFIX.console" \
        --stderr "$LOG_PREFIX.console" \
        --env "SS_DEVFLOW_TRACE=$LOG_PREFIX.devflow-trace" \
        --args "${launch_arguments[@]}"

    resolve_new_app_pid "$APP_BINARY" "$launch_token" "$pids_before"
    verify_process_identity "$APP_PID" "$APP_BINARY" ||
        {
            fail "New validator PID failed executable and bundle identity verification."
            return 1
        }
    echo "Validator process captured before DevFlow wait: pid=$APP_PID executable=$APP_BINARY"

    echo "Waiting for the DevFlow agent on port $devflow_port."
    elapsed=0
    while [[ "$elapsed" -lt "$WAIT_TIMEOUT" ]]; do
        if maui devflow agent status --agent-port "$devflow_port" \
            > "$LOG_PREFIX.agent-status" 2>&1 &&
            grep -q '"running": true' "$LOG_PREFIX.agent-status"; then
            agent_connected=true
            break
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done
    if [[ "$agent_connected" != "true" ]]; then
        tail -40 "$LOG_PREFIX.console" || true
        fail "DevFlow agent did not connect on port $devflow_port within ${WAIT_TIMEOUT}s."
        return 1
    fi

    verify_process_identity "$APP_PID" "$APP_BINARY" ||
        {
            fail "Validator process identity changed while waiting for DevFlow."
            return 1
        }
    verify_process_port_association "$APP_PID" "$devflow_port"
    process_command=$(ps -ww -p "$APP_PID" -o command= | sed -E 's/^[[:space:]]+//')
    [[ "$process_command" == "$APP_BINARY "* &&
       " $process_command " == *" --migration-validation-launch-token $launch_token "* ]] ||
        {
            fail "DevFlow belongs to an unexpected executable or launch token: $process_command"
            return 1
        }

    maui devflow list --json > "$LOG_PREFIX.agent-list" 2>&1
    agent_identity=$(verify_agent_identity \
        "$LOG_PREFIX.agent-list" "$PROJECT" "$TFM" "$devflow_port")
    echo "DevFlow identity verified: $agent_identity"

    echo "Waiting for migration, path, and sync-suppression signals."
    elapsed=0
    while [[ "$elapsed" -lt "$SIGNAL_TIMEOUT" ]]; do
        if grep -Fq "$SANITY_SIGNAL" "$LOG_PREFIX.console" &&
            grep -Fq "$VALIDATION_PATH_SIGNAL $disposable_db" "$LOG_PREFIX.console" &&
            grep -F "$OPENED_BINDING_SIGNAL main=$disposable_db device=$database_device inode=$database_inode fd=" "$LOG_PREFIX.console" |
                grep -Eq 'fd=[0-9]+$' &&
            grep -Fq "$SYNC_SUPPRESSION_SIGNAL" "$LOG_PREFIX.console"; then
            signals_ready=true
            break
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done
    if [[ "$signals_ready" != "true" ]]; then
        tail -60 "$LOG_PREFIX.console" || true
        fail "Required migration-validation signals did not arrive within ${SIGNAL_TIMEOUT}s."
        return 1
    fi

    if [[ -e "$marker_file" ||
          -e "$validation_root/.sentencestudio-migration-validation.consumed" ]]; then
        fail "One-shot validation marker was not atomically consumed."
        return 1
    fi
    if grep -Fq "$live_db" "$LOG_PREFIX.console"; then
        fail "Native app log references the live database path."
        return 1
    fi
    if grep -iE "$FORBIDDEN_STARTUP_PATTERN" "$LOG_PREFIX.console"; then
        fail "Automatic network or authentication startup ran in migration-validation mode."
        return 1
    fi

    if maui devflow logs --source native --limit 500 --agent-port "$devflow_port" \
        > "$LOG_PREFIX.devflow-raw" 2>&1; then
        filter_devflow_log_for_validation_run \
            "$LOG_PREFIX.devflow-raw" \
            "$LOG_PREFIX.devflow" \
            "$OPENED_BINDING_SIGNAL main=$disposable_db device=$database_device inode=$database_inode fd="
    else
        : > "$LOG_PREFIX.devflow"
    fi
    echo "Validating native logs."
    validate_migration_logs "$LOG_PREFIX.console" "$LOG_PREFIX.devflow"

    stop_app

    echo "Validating the pending-to-applied transition and complete schema."
    validate_disposable_database "$disposable_db" "$latest_migration"
    local post_latest_count
    post_latest_count=$(sqlite_readonly "$disposable_db" \
        "PRAGMA query_only=ON; SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='$latest_migration';")
    [[ "$post_latest_count" == "1" ]] ||
        {
            fail "Latest migration history did not transition from zero to exactly one."
            return 1
        }
    echo "Migration history transition verified: $latest_migration 0 -> 1."

    echo "Rechecking the live database through stable read-only access."
    capture_readonly_source_state "$live_db" "$source_after"
    verify_source_unchanged "$source_before" "$source_after"

    echo "Migration validation passed on $TFM."
    echo "Disposable database: $disposable_db"
    echo "Live database was not used as migration input and is unchanged: $live_db"
    echo "Validation artifacts retained: $LOG_DIR"
}

main() {
    trap on_exit EXIT

    case "${1:-}" in
        --validate-logs-only)
            [[ $# -ge 2 && $# -le 3 ]] ||
                {
                    echo "Usage: $0 --validate-logs-only <console-log> [devflow-log]" >&2
                    return 2
                }
            validate_migration_logs "$2" "${3:-}"
            ;;
        --validate-database-only)
            [[ $# -ge 2 && $# -le 3 ]] ||
                {
                    echo "Usage: $0 --validate-database-only <database> [migration-id]" >&2
                    return 2
                }
            validate_disposable_database "$2" "${3:-$(discover_latest_migration_id)}"
            ;;
        --validate-isolation-only)
            [[ $# -eq 4 ]] ||
                {
                    echo "Usage: $0 --validate-isolation-only <root> <database> <live-database>" >&2
                    return 2
                }
            initialize_physical_temp_root
            validate_isolation_paths "$2" "$3" "$4" >/dev/null
            ;;
        --test-early-timeout-cleanup)
            [[ $# -eq 1 ]] ||
                {
                    echo "Usage: $0 --test-early-timeout-cleanup" >&2
                    return 2
                }
            run_early_timeout_cleanup_test_hook
            ;;
        --test-pid-gap-cleanup)
            [[ $# -eq 1 ]] ||
                {
                    echo "Usage: $0 --test-pid-gap-cleanup" >&2
                    return 2
                }
            run_pid_gap_cleanup_test_hook
            ;;
        "")
            run_full_validation
            ;;
        *)
            echo "Usage: $0 [--validate-logs-only ...|--validate-database-only ...|--validate-isolation-only ...]" >&2
            return 2
            ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
