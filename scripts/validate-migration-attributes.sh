#!/usr/bin/env bash
set -euo pipefail
# ---------------------------------------------------------------------------
# validate-migration-attributes.sh
#
# Catches the RECURRING dual-provider migration bug (2026-05-03 RefreshToken,
# 2026-07-02 ActivitySession): a hand-written EF Core migration whose class is
# MISSING the [Migration("<id>")] attribute. Without it, EF never discovers the
# migration in that provider's migrations assembly, so MigrateAsync SILENTLY
# skips it. The PostgreSQL side usually has the attribute (works on webapp/prod),
# so the failure is invisible until a native head (iOS/Android/macOS SQLite)
# hits the missing table/column.
#
# This check is STATIC, DETERMINISTIC, and DEVICE-FREE. It runs in CI on every
# push/PR and should be run locally before any mobile publish. A raw-DDL test
# CANNOT catch this bug (the SQL is valid; the migration is just never invoked),
# which is exactly why it kept slipping through.
# ---------------------------------------------------------------------------

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PG_DIR="$ROOT/src/SentenceStudio.Shared/Migrations"
SQ_DIR="$ROOT/src/SentenceStudio.Shared/Migrations/Sqlite"

fail=0

check_dir() {
  local dir="$1" label="$2"
  [ -d "$dir" ] || { echo "  (no $label migrations dir at $dir)"; return; }
  for f in "$dir"/*_*.cs; do
    [ -e "$f" ] || continue
    case "$f" in
      *Designer.cs) continue ;;
      *ModelSnapshot*) continue ;;
    esac
    local id base designer
    base="$(basename "$f" .cs)"
    id="$base"
    designer="$dir/$base.Designer.cs"

    # [Migration("<id>")] must be discoverable in the .cs OR its .Designer.cs
    if ! { grep -hq "Migration(\"$id\")" "$f" 2>/dev/null || \
           { [ -f "$designer" ] && grep -hq "Migration(\"$id\")" "$designer" 2>/dev/null; }; }; then
      echo "  ❌ $label: $base — MISSING [Migration(\"$id\")] attribute"
      echo "       EF will NOT discover or apply this migration. Add to the class:"
      echo "         [DbContext(typeof(ApplicationDbContext))]"
      echo "         [Migration(\"$id\")]"
      echo "       (plus 'using Microsoft.EntityFrameworkCore.Infrastructure;' and 'using SentenceStudio.Data;')"
      fail=1
      continue
    fi

    # [DbContext(...)] must also be present somewhere in the pair
    if ! { grep -hq "DbContext(" "$f" 2>/dev/null || \
           { [ -f "$designer" ] && grep -hq "DbContext(" "$designer" 2>/dev/null; }; }; then
      echo "  ❌ $label: $base — MISSING [DbContext(typeof(ApplicationDbContext))] attribute"
      fail=1
    fi
  done
}

echo "🔎 Validating EF migration [Migration]/[DbContext] attributes..."
echo "--- PostgreSQL (Migrations/) ---"
check_dir "$PG_DIR" "PostgreSQL"
echo "--- SQLite (Migrations/Sqlite/) ---"
check_dir "$SQ_DIR" "SQLite"

# Warn (do not fail) when a PostgreSQL migration has no same-id SQLite counterpart.
# Some migrations are intentionally provider-specific (e.g. *PgDate*, InitialPostgreSQL).
echo "--- Cross-provider pairing (warnings only) ---"
if [ -d "$PG_DIR" ] && [ -d "$SQ_DIR" ]; then
  for f in "$PG_DIR"/*_*.cs; do
    [ -e "$f" ] || continue
    case "$f" in *Designer.cs) continue ;; *ModelSnapshot*) continue ;; esac
    base="$(basename "$f" .cs)"
    if [ ! -f "$SQ_DIR/$base.cs" ]; then
      echo "  ⚠️  PostgreSQL migration '$base' has no SQLite counterpart — confirm it is intentionally Postgres-only."
    fi
  done
fi

echo
if [ "$fail" -ne 0 ]; then
  echo "❌ Migration attribute validation FAILED. Fix the migrations above before shipping."
  echo "   See .squad/skills/ef-dual-provider-migrations/SKILL.md."
  exit 1
fi
echo "✅ All migrations carry discoverable [Migration]/[DbContext] attributes."
