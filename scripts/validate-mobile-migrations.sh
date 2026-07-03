#!/usr/bin/env bash
set -euo pipefail
# Validates that migrations apply cleanly on a real mobile TFM build.
# Builds Mac Catalyst Debug, launches via maui devflow, watches logs for
# migration errors, fails if any are found. Intended as a pre-deploy gate.

TFM="net11.0-maccatalyst"
PROJECT="src/SentenceStudio.MacCatalyst/SentenceStudio.MacCatalyst.csproj"
LOG_DIR=$(mktemp -d -t ss-migration-XXXX)
LOG_PREFIX="$LOG_DIR/migration-validation"

echo "📦 Building $TFM..."
dotnet build "$PROJECT" -f "$TFM" -c Debug > "$LOG_PREFIX.build" 2>&1 || {
    echo "❌ Build failed. See $LOG_PREFIX.build"
    cat "$LOG_PREFIX.build"
    exit 1
}
echo "✅ Build succeeded"

echo "🚀 Launching app in background..."
dotnet build "$PROJECT" -f "$TFM" -t:Run -c Debug > "$LOG_PREFIX.run" 2>&1 &
RUN_PID=$!
trap "kill $RUN_PID 2>/dev/null || true; echo '🧹 Cleaned up background process'" EXIT

echo "⏳ Waiting for maui devflow agent to connect (timeout: 120s)..."
maui devflow wait --timeout 120 || {
    echo "❌ Agent did not connect within 120s"
    echo "Build output:"
    tail -50 "$LOG_PREFIX.run"
    exit 1
}
echo "✅ Agent connected"

echo "⏳ Giving app 15s to complete startup + migrations..."
sleep 15

echo "📋 Fetching native logs (last 500 lines)..."
maui devflow MAUI logs --source native --limit 500 > "$LOG_PREFIX.native" 2>&1 || {
    echo "❌ Could not fetch native logs via maui devflow."
    echo "   This usually means DevFlow attached to the WRONG app (another running"
    echo "   DevFlow app on this machine) or the SentenceStudio app never started."
    echo "   A migration gate that cannot read the app's logs validates NOTHING —"
    echo "   failing instead of pretending success. Close other DevFlow apps and retry."
    echo "   Logs saved to: $LOG_PREFIX.native"
    exit 1
}

echo "🔍 Scanning for migration/schema errors..."
if grep -iE "SQLite Error|MigrateAsync failed|Failed to initialize CoreSync|sanity check failed|no such column|no such table" "$LOG_PREFIX.native" 2>/dev/null; then
    echo ""
    echo "❌ Migration errors detected! Full log context:"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    cat "$LOG_PREFIX.native"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo ""
    echo "Logs saved to: $LOG_PREFIX.native"
    exit 1
fi

# Require the POSITIVE sanity signal. Absence is a FAILURE, not a warning:
# a grep-for-errors over an empty/unfetched log finds nothing and would otherwise
# "pass" while validating nothing (the DevFlow stale-agent false-pass, 2026-07-02).
# If DevFlow attached to the wrong app, our PASSED line will NOT be in the log,
# so this check also catches the wrong-app case.
if grep -q "Mobile schema sanity check PASSED" "$LOG_PREFIX.native" 2>/dev/null; then
    echo "✅ Schema sanity check passed"
else
    echo ""
    echo "❌ Positive sanity signal 'Mobile schema sanity check PASSED' NOT found."
    echo "   The app did not start + migrate, the logs were truncated/unfetchable, or"
    echo "   DevFlow attached to a DIFFERENT app. This is a FAILURE — a green grep over"
    echo "   an empty log proves nothing. Do NOT trust a 'pass' without this signal."
    echo "   Confirm with 'maui devflow diagnose' that the attached project is"
    echo "   SentenceStudio (not another DevFlow app), then re-run."
    echo "   Logs saved to: $LOG_PREFIX.native"
    exit 1
fi

echo ""
echo "✅ Mobile migrations validated on $TFM — errors scan clean AND sanity signal present"
echo "   Logs saved to: $LOG_DIR"
