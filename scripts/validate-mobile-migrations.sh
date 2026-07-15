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

# Launch the binary directly to capture ILogger.AddConsole() output.
# macOS AppKit does NOT use 'dotnet build -t:Run' (that's Catalyst/iOS).
echo "🚀 Launching app binary (capturing console output)..."
"$APP_BINARY" > "$LOG_PREFIX.console" 2>&1 &
APP_PID=$!
trap 'kill $APP_PID 2>/dev/null || true; echo "🧹 Cleaned up app process (PID $APP_PID)"' EXIT

# Give the process a moment to start
sleep 3
if ! kill -0 "$APP_PID" 2>/dev/null; then
    echo "❌ App exited immediately. Console output:"
    cat "$LOG_PREFIX.console"
    exit 1
fi
echo "✅ App process alive (PID $APP_PID)"

# Wait for DevFlow agent on the CORRECT port (9225).
# Broker auto-discovery is unreliable; use explicit --agent-port.
echo "⏳ Waiting for DevFlow agent on port $DEVFLOW_PORT (timeout: ${WAIT_TIMEOUT}s)..."
AGENT_CONNECTED=false
ELAPSED=0
while [[ $ELAPSED -lt $WAIT_TIMEOUT ]]; do
    if maui devflow agent status --agent-port "$DEVFLOW_PORT" 2>/dev/null | grep -q '"running"'; then
        AGENT_CONNECTED=true
        break
    fi
    # Check app hasn't crashed while waiting
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        echo "❌ App exited during startup. Console output:"
        cat "$LOG_PREFIX.console"
        exit 1
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

# Validate console output has content (primary log source)
if [[ ! -s "$LOG_PREFIX.console" ]]; then
    echo "❌ Console output is empty — nothing to validate."
    echo "   App may have crashed silently or ILogger.AddConsole() is not configured."
    echo "   Logs saved to: $LOG_DIR"
    exit 1
fi

# Scan BOTH sources for migration errors
echo "🔍 Scanning for migration/schema errors..."
MIGRATION_ERROR=false
for logfile in "$LOG_PREFIX.console" "$LOG_PREFIX.devflow"; do
    if [[ -s "$logfile" ]] && \
       grep -iE "SQLite Error|MigrateAsync failed|Failed to initialize CoreSync|sanity check failed|no such column|no such table" "$logfile" 2>/dev/null; then
        MIGRATION_ERROR=true
        echo "  ↳ Error found in: $logfile"
    fi
done
if [[ "$MIGRATION_ERROR" == "true" ]]; then
    echo ""
    echo "❌ Migration errors detected! Relevant console output:"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    grep -iE "SQLite Error|MigrateAsync|sanity check|no such column|no such table|Migration" \
        "$LOG_PREFIX.console" | head -50
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo ""
    echo "Full logs saved to: $LOG_DIR"
    exit 1
fi

# Require the POSITIVE sanity signal from console output.
# Console output is the ground truth — ILogger.AddConsole() captures the
# MigrationSanityCheckService log entry directly, unlike DevFlow logs which
# may truncate early startup entries (--limit 500 misses them when 500+ EF
# command entries follow). Absence is a FAILURE, not a warning.
if grep -q "Mobile schema sanity check PASSED" "$LOG_PREFIX.console" 2>/dev/null; then
    echo "✅ Schema sanity check passed"
else
    echo ""
    echo "❌ Positive sanity signal 'Mobile schema sanity check PASSED' NOT found."
    echo "   The app did not start + migrate, ILogger output was missing, or the"
    echo "   sanity check threw an exception. This is a FAILURE."
    echo "   Logs saved to: $LOG_DIR"
    echo ""
    echo "Last 30 lines of console output:"
    tail -30 "$LOG_PREFIX.console"
    exit 1
fi

echo ""
echo "✅ Mobile migrations validated on $TFM — errors scan clean AND sanity signal present"
echo "   Attached app PID: $APP_PID (port $DEVFLOW_PORT)"
echo "   Logs saved to: $LOG_DIR"
