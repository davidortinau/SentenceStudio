#!/usr/bin/env bash
set -euo pipefail
# Validates that migrations apply cleanly on a real native TFM build.
# Uses the macOS AppKit head (Captain's default native surface).
# Builds Debug, launches the binary to capture console output, verifies
# DevFlow agent identity, scans for migration errors, and requires the
# positive "Mobile schema sanity check PASSED" signal.
# Intended as a pre-deploy gate.
#
# Fixed 2026-07-14: Five compounding defects in prior version:
#   1. Invalid CLI command: "maui devflow MAUI logs" (MAUI fuzzy-matched to ui)
#   2. Wrong project/TFM: targeted MacCatalyst, not macOS AppKit
#   3. Wrong launch model: -t:Run doesn't work for macOS AppKit
#   4. Missing ValidateXcodeVersion=false for Xcode 26.4 / Preview 5
#   5. No app identity check; no explicit --agent-port (wrong-app attachment)

TFM="net11.0-macos"
PROJECT="src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj"
# Port matches MacOSMauiProgram.cs: builder.AddMauiDevFlowAgent(options => { options.Port = 9225; });
DEVFLOW_PORT=9225
WAIT_TIMEOUT=90
STARTUP_SETTLE=20

MIGRATION_ERROR_PATTERN="FATAL: Database migration failed|FATAL: SyncService initialization failed completely|FATAL ERROR in database initialization|Mobile schema sanity check FAILED|no such column|no such table"
SQLITE_ERROR_PATTERN="SQLite Error"
SANITY_SIGNAL="Mobile schema sanity check PASSED"

validate_migration_logs() {
    local console_log="$1"
    local devflow_log="${2:-}"
    local sanity_line=""
    local migration_error=false
    local logfile

    if [[ ! -s "$console_log" ]]; then
        echo "❌ Console output is empty — nothing to validate."
        echo "   App may have crashed silently or ILogger.AddConsole() is not configured."
        return 1
    fi

    sanity_line=$(grep -n -m1 "$SANITY_SIGNAL" "$console_log" 2>/dev/null |
        cut -d: -f1 || true)

    # Explicit migration/schema failures are always fatal, even if they appear
    # after the sanity signal or only in the supplementary DevFlow log.
    for logfile in "$console_log" "$devflow_log"; do
        if [[ -n "$logfile" && -s "$logfile" ]] && \
           grep -iE "$MIGRATION_ERROR_PATTERN" "$logfile" 2>/dev/null; then
            migration_error=true
            echo "  ↳ Migration/schema error found in: $logfile"
        fi
    done

    if [[ -n "$sanity_line" ]]; then
        # Console output is chronological and authoritative. Once schema sanity
        # passes, later generic SQLite errors belong to runtime work (for example
        # CoreSync), not migration validation. DevFlow logs are supplementary and
        # newest-first, so they cannot safely establish this boundary.
        if sed -n "1,${sanity_line}p" "$console_log" |
            grep -iE "$SQLITE_ERROR_PATTERN"; then
            migration_error=true
            echo "  ↳ SQLite error found before schema sanity passed: $console_log"
        fi
    else
        # Without the positive boundary, fail closed and scan every available
        # line for generic SQLite errors before reporting the missing signal.
        for logfile in "$console_log" "$devflow_log"; do
            if [[ -n "$logfile" && -s "$logfile" ]] && \
               grep -iE "$SQLITE_ERROR_PATTERN" "$logfile" 2>/dev/null; then
                migration_error=true
                echo "  ↳ SQLite error found in unbounded log: $logfile"
            fi
        done
    fi

    if [[ "$migration_error" == "true" ]]; then
        return 1
    fi

    if [[ -z "$sanity_line" ]]; then
        echo "❌ Positive sanity signal '$SANITY_SIGNAL' NOT found."
        return 1
    fi

    return 0
}

if [[ "${1:-}" == "--validate-logs-only" ]]; then
    if [[ $# -lt 2 || $# -gt 3 ]]; then
        echo "Usage: $0 --validate-logs-only <console-log> [devflow-log]" >&2
        exit 2
    fi
    validate_migration_logs "$2" "${3:-}"
    exit $?
fi

LOG_DIR=$(mktemp -d -t ss-migration-XXXX)
LOG_PREFIX="$LOG_DIR/migration-validation"

echo "📦 Building $TFM (ValidateXcodeVersion=false for Xcode 26.4)..."
dotnet build "$PROJECT" -f "$TFM" -c Debug \
    -p:ValidateXcodeVersion=false \
    > "$LOG_PREFIX.build" 2>&1 || {
    echo "❌ Build failed. See $LOG_PREFIX.build"
    tail -30 "$LOG_PREFIX.build"
    exit 1
}
echo "✅ Build succeeded"

# Find the .app bundle produced by the build
APP_BUNDLE=$(find "$(dirname "$PROJECT")/bin/Debug/$TFM" -name "*.app" -maxdepth 3 2>/dev/null | head -1)
if [[ -z "$APP_BUNDLE" ]]; then
    echo "❌ No .app bundle found under $(dirname "$PROJECT")/bin/Debug/$TFM"
    exit 1
fi
APP_BINARY="$APP_BUNDLE/Contents/MacOS/SentenceStudio"
if [[ ! -x "$APP_BINARY" ]]; then
    echo "❌ App binary not executable: $APP_BINARY"
    exit 1
fi
echo "📱 App bundle: $APP_BUNDLE"

# AppKit apps must launch through LaunchServices. Starting the bundle executable directly can
# trigger a SIGKILL even when the ad-hoc signature produced by the build is valid.
echo "🚀 Launching app bundle through LaunchServices (capturing console output)..."
open -n "$APP_BUNDLE" \
    --stdout "$LOG_PREFIX.console" \
    --stderr "$LOG_PREFIX.console"

APP_PID=""
cleanup() {
    if [[ "$APP_PID" =~ ^[0-9]+$ ]]; then
        kill "$APP_PID" 2>/dev/null || true
        echo "🧹 Cleaned up app process (PID $APP_PID)"
    fi
}
trap cleanup EXIT

# Wait for DevFlow agent on the CORRECT port (9225).
# Broker auto-discovery is unreliable; use explicit --agent-port.
echo "⏳ Waiting for DevFlow agent on port $DEVFLOW_PORT (timeout: ${WAIT_TIMEOUT}s)..."
AGENT_CONNECTED=false
ELAPSED=0
while [[ $ELAPSED -lt $WAIT_TIMEOUT ]]; do
    if maui devflow agent status --agent-port "$DEVFLOW_PORT" 2>/dev/null |
        grep -q '"running": true'; then
        AGENT_CONNECTED=true
        APP_PID=$(lsof -tiTCP:"$DEVFLOW_PORT" -sTCP:LISTEN | head -1)
        break
    fi
    sleep 5
    ELAPSED=$((ELAPSED + 5))
done

if [[ "$AGENT_CONNECTED" != "true" ]]; then
    echo "❌ DevFlow agent did not connect on port $DEVFLOW_PORT within ${WAIT_TIMEOUT}s"
    echo "Console output (last 30 lines):"
    tail -30 "$LOG_PREFIX.console"
    exit 1
fi
echo "✅ Agent connected on port $DEVFLOW_PORT"

if [[ ! "$APP_PID" =~ ^[0-9]+$ ]]; then
    echo "❌ DevFlow connected, but the listener PID could not be resolved safely."
    exit 1
fi
echo "✅ App process alive (PID $APP_PID)"

# Verify app identity — prevent the stale-agent false-pass (2026-07-02).
echo "🔍 Verifying attached app identity..."
AGENT_JSON=$(maui devflow agent status --agent-port "$DEVFLOW_PORT" 2>&1)
echo "$AGENT_JSON"
# The SentenceStudio macOS agent uses the standard MAUI DevFlow agent
# (not Comet). If we see a framework other than "maui", warn loudly.
if echo "$AGENT_JSON" | grep -q '"framework": "comet"'; then
    echo "❌ Agent is Comet (wrong app). Close other DevFlow apps and retry."
    exit 1
fi
echo "✅ Agent identity verified (not a stale/foreign agent)"

echo "⏳ Giving app ${STARTUP_SETTLE}s to complete startup + migrations..."
sleep "$STARTUP_SETTLE"

# Collect logs from two sources:
# 1. Console output (ILogger.AddConsole() — captures ALL log entries including sanity check)
# 2. DevFlow logs (structured JSON — may not include all categories/entries)
# The console output is always the primary source since it captures the complete
# startup sequence. DevFlow logs (--limit 500) can miss the sanity check if 500+
# entries are emitted after it during EF Core command logging.

echo "📋 Fetching supplementary DevFlow logs (port $DEVFLOW_PORT)..."
if maui devflow logs --source native --limit 500 --agent-port "$DEVFLOW_PORT" \
    > "$LOG_PREFIX.devflow" 2>&1; then
    if ! grep -q '"unimplemented"' "$LOG_PREFIX.devflow" 2>/dev/null && \
       [[ -s "$LOG_PREFIX.devflow" ]]; then
        echo "✅ DevFlow logs fetched (supplementary)"
    else
        echo "⚠️  DevFlow logs returned 'unimplemented' — using console output only"
    fi
fi

# Validate the primary console log and supplementary DevFlow log. Generic
# SQLite errors are migration-fatal only through the first positive sanity
# signal; explicit migration/schema failures remain fatal everywhere.
echo "🔍 Scanning for migration/schema errors..."
if ! validate_migration_logs "$LOG_PREFIX.console" "$LOG_PREFIX.devflow"; then
    echo ""
    echo "❌ Migration validation failed! Relevant console output:"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    grep -iE "$MIGRATION_ERROR_PATTERN|$SQLITE_ERROR_PATTERN|$SANITY_SIGNAL" \
        "$LOG_PREFIX.console" | head -50
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo ""
    echo "Full logs saved to: $LOG_DIR"
    exit 1
fi

echo "✅ Schema sanity check passed"

echo ""
echo "✅ Mobile migrations validated on $TFM — errors scan clean AND sanity signal present"
echo "   Attached app PID: $APP_PID (port $DEVFLOW_PORT)"
echo "   Logs saved to: $LOG_DIR"
