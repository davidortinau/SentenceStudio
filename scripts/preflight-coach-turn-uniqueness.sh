#!/usr/bin/env bash
set -euo pipefail
# Descriptive preflight for 20260819130000_AddCoachWriteOperationTurnUniqueness.
#
# The migration creates a UNIQUE index on CoachWriteOperation(UserProfileId, ConversationId,
# TurnId). If a target database somehow holds two rows sharing a turn, the migration fails — which
# is the correct outcome, because the alternative is deleting one of a learner's operations to make
# an index build. This script does NOT fix anything. It answers "would it fail, and on what?" so
# the answer arrives before the deploy rather than from a half-migrated database.
#
# Deliberately read-only. No DELETE, no UPDATE, no DDL. Duplicate rows, if any ever appear, are a
# decision for a human with the learner in the loop.
#
# Usage:
#   scripts/preflight-coach-turn-uniqueness.sh "Host=...;Username=...;Password=...;Database=..."
#   COACH_DB_CONNECTION=... scripts/preflight-coach-turn-uniqueness.sh
#
# Exit codes:
#   0  safe to migrate (or the table does not exist yet, which is also safe)
#   1  duplicates found — the migration would fail; nothing was changed
#   2  could not check (no connection string, psql missing, server unreachable)

CONNECTION="${1:-${COACH_DB_CONNECTION:-${COACH_PG_TEST_CONNECTION:-}}}"

if [[ -z "$CONNECTION" ]]; then
    echo "❌ No connection string. Pass one as \$1 or set COACH_DB_CONNECTION."
    exit 2
fi

if ! command -v psql >/dev/null 2>&1; then
    echo "❌ psql not found. Install the PostgreSQL client to run this preflight."
    exit 2
fi

# Accept an ADO.NET-style connection string as well as a URI, since that is what the app config
# holds and what an operator will have to hand.
to_uri() {
    local cs="$1"
    if [[ "$cs" == postgres://* || "$cs" == postgresql://* ]]; then
        echo "$cs"
        return
    fi

    local host port user pass db
    host=$(sed -n 's/.*[Hh]ost=\([^;]*\).*/\1/p' <<<"$cs")
    port=$(sed -n 's/.*[Pp]ort=\([^;]*\).*/\1/p' <<<"$cs")
    user=$(sed -n 's/.*[Uu]sername=\([^;]*\).*/\1/p' <<<"$cs")
    pass=$(sed -n 's/.*[Pp]assword=\([^;]*\).*/\1/p' <<<"$cs")
    db=$(sed -n 's/.*[Dd]atabase=\([^;]*\).*/\1/p' <<<"$cs")
    port="${port:-5432}"
    echo "postgresql://${user}:${pass}@${host}:${port}/${db}"
}

URI=$(to_uri "$CONNECTION")

echo "🔎 Coach turn-uniqueness preflight"

TABLE_EXISTS=$(psql "$URI" -tAc \
    "SELECT to_regclass('public.\"CoachWriteOperation\"') IS NOT NULL" 2>/dev/null) || {
    echo "❌ Could not reach the database."
    exit 2
}

if [[ "$TABLE_EXISTS" != "t" ]]; then
    echo "✅ CoachWriteOperation does not exist yet — the migration creates it clean. Safe."
    exit 0
fi

INDEX_EXISTS=$(psql "$URI" -tAc \
    "SELECT count(*) FROM pg_indexes
     WHERE tablename = 'CoachWriteOperation'
       AND indexname = 'IX_CoachWriteOperation_UserProfileId_ConversationId_TurnId'")

if [[ "$INDEX_EXISTS" == "1" ]]; then
    echo "✅ The unique index is already applied. Nothing to check."
    exit 0
fi

# NULL turn identities are excluded on purpose: PostgreSQL treats them as distinct, so rows
# predating the turn requirement neither collide nor block the index.
DUPES=$(psql "$URI" -tAc \
    "SELECT count(*) FROM (
        SELECT 1 FROM \"CoachWriteOperation\"
        WHERE \"TurnId\" IS NOT NULL
        GROUP BY \"UserProfileId\", \"ConversationId\", \"TurnId\"
        HAVING count(*) > 1
     ) d")

if [[ "$DUPES" == "0" ]]; then
    echo "✅ No turn is shared by two operations. The unique index will build."
    exit 0
fi

echo "❌ $DUPES turn(s) are shared by more than one operation."
echo "   The migration will fail on the index build and change nothing."
echo
echo "   Affected turns (identifiers only, no learner content):"
psql "$URI" -c \
    "SELECT \"ConversationId\", \"TurnId\", count(*) AS operations,
            min(\"CreatedAtUtc\") AS first_seen, max(\"CreatedAtUtc\") AS last_seen
     FROM \"CoachWriteOperation\"
     WHERE \"TurnId\" IS NOT NULL
     GROUP BY \"UserProfileId\", \"ConversationId\", \"TurnId\"
     HAVING count(*) > 1
     ORDER BY count(*) DESC
     LIMIT 50"
echo
echo "   This script will not resolve them. Each duplicate is a real operation on a real"
echo "   learner's data, and choosing which one survives is not a deploy step."
exit 1
