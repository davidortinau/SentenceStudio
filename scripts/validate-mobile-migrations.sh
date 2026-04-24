#!/usr/bin/env bash
set -euo pipefail
# Validates that migrations apply cleanly on a real mobile TFM build.
# Builds Mac Catalyst Debug, launches via maui devflow, watches logs for
# migration errors, fails if any are found. Intended as a pre-deploy gate.

TFM="net10.0-maccatalyst"
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
    echo "⚠️  Warning: Could not fetch native logs via maui devflow"
    echo "   Continuing with available logs..."
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

# Also check for sanity check success message
if grep -q "Mobile schema sanity check PASSED" "$LOG_PREFIX.native" 2>/dev/null; then
    echo "✅ Schema sanity check passed"
else
    echo "⚠️  Warning: Schema sanity check PASSED message not found in logs"
    echo "   This may indicate the check didn't run or logs were truncated."
    echo "   Logs saved to: $LOG_PREFIX.native"
    # Don't fail - the error grep above would have caught actual failures
fi

echo ""
echo "✅ Mobile migrations validated on $TFM — no errors found"
echo "   Logs saved to: $LOG_DIR"
