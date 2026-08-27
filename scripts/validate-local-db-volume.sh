#!/usr/bin/env bash
set -uo pipefail
# ---------------------------------------------------------------------------
# validate-local-db-volume.sh
#
# Guards against a RECURRING data-visibility bug: an AppHost started without
# LocalDb:DataVolume falls back to Aspire's auto-named, path-derived volume.
# The stack still comes up, https://localhost:7071 still answers, and the
# database behind it is empty or belongs to an unrelated lineage. Because the
# postgres container is ContainerLifetime.Persistent, that wrong container then
# survives for days and every later restart quietly reuses it.
#
# Earning event: 2026-08-20 -- Captain's dave@ortinau.com environment vanished
# from https://localhost:7071 for the Nth time. AppHost PID 58590 had been
# relaunched as:
#     Coach__SamWriteTools__Enabled=true nohup dotnet run --no-build --no-restore
# with no LocalDb__DataVolume, so Aspire mounted
# sentencestudio.apphost-461cee1bad-db-data (agent lineage, no dave@ortinau.com)
# while sentencestudio-local-crispy-barnacle-db-data (Captain's real data) sat
# unmounted at LINKS 0.
#
# Two modes:
#   (default)   STATIC  -- greps AppHost.cs for the fail-fast guard. Device-free,
#                          deterministic, CI-safe. Belongs alongside
#                          validate-azurite-persistence.sh.
#   --runtime   RUNTIME -- inspects the live Docker container and asserts it
#                          mounts the volume you expect, and optionally that a
#                          known account is present. Read-only: it runs
#                          docker inspect and SELECT only.
#
# Usage:
#   scripts/validate-local-db-volume.sh
#   scripts/validate-local-db-volume.sh --runtime
#   scripts/validate-local-db-volume.sh --runtime \
#       --expect-volume sentencestudio-local-crispy-barnacle-db-data \
#       --expect-account dave@ortinau.com
#
# Exit codes: 0 pass, 1 fail, 2 usage/precondition error.
# ---------------------------------------------------------------------------

ROOT="${VALIDATE_ROOT_OVERRIDE:-$(cd "$(dirname "$0")/.." && pwd)}"
APPHOST="$ROOT/src/SentenceStudio.AppHost/AppHost.cs"

MODE="static"
EXPECT_VOLUME=""
EXPECT_ACCOUNT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --runtime)        MODE="runtime" ;;
    --expect-volume)  EXPECT_VOLUME="${2:-}"; shift ;;
    --expect-account) EXPECT_ACCOUNT="${2:-}"; shift ;;
    -h|--help)        sed -n '2,40p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

fail=0

# --------------------------------------------------------------------------
# STATIC CHECKS -- the guard must exist in AppHost.cs and must not be defanged.
# --------------------------------------------------------------------------
if [ ! -f "$APPHOST" ]; then
  echo "FAIL: AppHost.cs not found at $APPHOST"
  exit 2
fi

if ! grep -q 'LocalDb:DataVolume' "$APPHOST"; then
  echo "FAIL: AppHost.cs no longer reads LocalDb:DataVolume -- the named-volume opt-in is gone."
  fail=1
fi

if ! grep -q 'LocalDb:AllowEphemeralVolume' "$APPHOST"; then
  echo "FAIL: AppHost.cs is missing the LocalDb:AllowEphemeralVolume opt-in."
  echo "      Without it there is no fail-fast guard, so a run with no volume configured"
  echo "      silently attaches a fresh auto-named volume. See docs/local-dev-database-volumes.md."
  fail=1
fi

if ! grep -q 'throw new InvalidOperationException' "$APPHOST"; then
  echo "FAIL: AppHost.cs no longer throws when LocalDb:DataVolume is unset."
  echo "      The guard must REFUSE to boot, not warn -- a warning scrolls past in a log nobody reads."
  fail=1
fi

# The bare WithDataVolume() fallback is only allowed inside the opt-in branch.
# If it appears without the AllowEphemeralVolume gate anywhere, the guard is bypassable.
if grep -q 'WithDataVolume()' "$APPHOST" && ! grep -q 'allowEphemeralLocalDb' "$APPHOST"; then
  echo "FAIL: AppHost.cs calls WithDataVolume() with no name and no allowEphemeralLocalDb gate."
  fail=1
fi

if [ "$MODE" = "static" ]; then
  if [ "$fail" -eq 0 ]; then
    echo "PASS: AppHost.cs fail-fast local-db-volume guard is intact."
  fi
  exit "$fail"
fi

# --------------------------------------------------------------------------
# RUNTIME CHECKS -- read-only inspection of the live stack.
# --------------------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  echo "FAIL: docker not on PATH; cannot run --runtime checks."
  exit 2
fi

# Find the running Aspire postgres container (image postgres:*, name db-*).
CONTAINER="$(docker ps --filter 'name=^db-' --format '{{.Names}}' 2>/dev/null | head -1)"
if [ -z "$CONTAINER" ]; then
  echo "FAIL: no running Aspire postgres container (name db-*) found. Is the stack up?"
  exit 1
fi

ACTUAL_VOLUME="$(docker inspect "$CONTAINER" \
  --format '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' 2>/dev/null)"

echo "container:      $CONTAINER"
echo "mounted volume: ${ACTUAL_VOLUME:-<none>}"

if [ -z "$ACTUAL_VOLUME" ]; then
  echo "FAIL: could not determine the data volume mounted by $CONTAINER."
  fail=1
fi

# If the caller did not name an expectation, fall back to the AppHost's own
# configured value so the check still has teeth in CI and in agent harnesses.
if [ -z "$EXPECT_VOLUME" ]; then
  EXPECT_VOLUME="${LocalDb__DataVolume:-}"
fi
if [ -z "$EXPECT_VOLUME" ] && command -v dotnet >/dev/null 2>&1; then
  EXPECT_VOLUME="$(dotnet user-secrets list --project "$ROOT/src/SentenceStudio.AppHost" 2>/dev/null \
    | awk -F' = ' '/^LocalDb:DataVolume/{print $2}')"
fi

if [ -n "$EXPECT_VOLUME" ]; then
  echo "expected volume: $EXPECT_VOLUME"
  if [ "$ACTUAL_VOLUME" != "$EXPECT_VOLUME" ]; then
    echo "FAIL: the running stack is on the WRONG database volume."
    echo "      expected: $EXPECT_VOLUME"
    echo "      actual:   $ACTUAL_VOLUME"
    echo "      Anything you verify against https://localhost:7071 right now is meaningless."
    echo "      Stop the AppHost, confirm LocalDb:DataVolume, remove the stale db-* CONTAINER"
    echo "      (never the volume), and restart. See docs/local-dev-database-volumes.md."
    fail=1
  else
    echo "OK: running stack is on the expected volume."
  fi
else
  echo "WARN: no expected volume supplied and none configured; volume identity unverified."
fi

# An auto-named volume is, by itself, the smoking gun for the recurring bug.
case "$ACTUAL_VOLUME" in
  *.apphost-*-db-data)
    echo "FAIL: $ACTUAL_VOLUME is an Aspire AUTO-NAMED volume (path-derived)."
    echo "      That is the exact failure mode this guard exists to catch: the stack looks"
    echo "      healthy while serving a database nobody intended to use."
    fail=1
    ;;
esac

# Optional account presence check -- read-only SELECT, reports a count only.
if [ -n "$EXPECT_ACCOUNT" ]; then
  if [ -z "${PGPASSWORD:-}" ]; then
    echo "WARN: PGPASSWORD not set; skipping account presence check for $EXPECT_ACCOUNT."
  else
    NORMALIZED="$(printf '%s' "$EXPECT_ACCOUNT" | tr '[:lower:]' '[:upper:]')"
    COUNT="$(docker exec -e PGPASSWORD="$PGPASSWORD" "$CONTAINER" \
      psql -U "${PGUSER:-dbadmin}" -d "${PGDATABASE:-sentencestudio}" -Atc \
      "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\"='${NORMALIZED}';" 2>/dev/null)"
    if [ "${COUNT:-0}" -ge 1 ] 2>/dev/null; then
      echo "OK: account $EXPECT_ACCOUNT present (count=$COUNT)."
    else
      echo "FAIL: account $EXPECT_ACCOUNT NOT found in the live database (count=${COUNT:-unknown})."
      echo "      The stack is almost certainly on the wrong volume."
      fail=1
    fi
  fi
fi

if [ "$fail" -eq 0 ]; then
  echo "PASS: live stack is on the intended local database."
fi
exit "$fail"
