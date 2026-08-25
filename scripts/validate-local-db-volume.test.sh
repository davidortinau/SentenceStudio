#!/usr/bin/env bash
set -uo pipefail
# ---------------------------------------------------------------------------
# validate-local-db-volume.test.sh
#
# Guard test for scripts/validate-local-db-volume.sh.
#
# This exists because the underlying bug is a REPEAT OFFENDER: Captain's
# dave@ortinau.com database has been silently swapped out from under a running
# https://localhost:7071 more than once. Per AGENTS.md, "if a bug came back, it
# needs a test so it can't come back again."
#
# Everything here is hermetic: `docker` and `dotnet` are stubbed onto PATH and
# AppHost.cs fixtures are written to a temp dir. No container is started, no
# volume is read, written, or removed. Safe to run while a stack is live.
# ---------------------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TARGET="$SCRIPT_DIR/validate-local-db-volume.sh"

pass=0
fail=0

ok()   { echo "  ok   - $1"; pass=$((pass + 1)); }
bad()  { echo "  FAIL - $1"; fail=$((fail + 1)); }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# --- fixture builders -------------------------------------------------------

make_root() {
  # $1 = root dir, $2 = AppHost.cs body
  mkdir -p "$1/src/SentenceStudio.AppHost"
  printf '%s\n' "$2" > "$1/src/SentenceStudio.AppHost/AppHost.cs"
}

GOOD_APPHOST='
var localDbDataVolume = builder.Configuration["LocalDb:DataVolume"]?.Trim();
var allowEphemeralLocalDb = string.Equals(builder.Configuration["LocalDb:AllowEphemeralVolume"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
if (string.IsNullOrWhiteSpace(localDbDataVolume))
{
    if (!allowEphemeralLocalDb) { throw new InvalidOperationException("refuse"); }
    c.WithDataVolume();
}
else { c.WithDataVolume(localDbDataVolume); }
'

# The pre-fix AppHost: reads the key, but silently falls back. This is the
# exact shape that lost Captain's database.
LEGACY_APPHOST='
var localDbDataVolume = builder.Configuration["LocalDb:DataVolume"]?.Trim();
if (string.IsNullOrWhiteSpace(localDbDataVolume)) { c.WithDataVolume(); }
else { c.WithDataVolume(localDbDataVolume); }
'

# Guard present in name only -- warns instead of throwing.
DEFANGED_APPHOST='
var localDbDataVolume = builder.Configuration["LocalDb:DataVolume"]?.Trim();
var allowEphemeralLocalDb = string.Equals(builder.Configuration["LocalDb:AllowEphemeralVolume"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
if (string.IsNullOrWhiteSpace(localDbDataVolume)) { Console.WriteLine("warning"); c.WithDataVolume(); }
else { c.WithDataVolume(localDbDataVolume); }
'

# --- docker / dotnet stubs --------------------------------------------------

make_stubs() {
  # $1 = bindir, $2 = container name (empty => no container), $3 = mounted volume
  mkdir -p "$1"
  cat > "$1/docker" <<STUB
#!/usr/bin/env bash
case "\$1" in
  ps)      printf '%s' "$2"; [ -n "$2" ] && echo ;;
  inspect) printf '%s\n' "$3" ;;
  exec)    printf '%s\n' "\${STUB_ACCOUNT_COUNT:-0}" ;;
esac
exit 0
STUB
  # dotnet stub: no user-secrets, so the script cannot pick up a real machine value.
  cat > "$1/dotnet" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
  chmod +x "$1/docker" "$1/dotnet"
}

run_target() {
  # runs the target with a stubbed PATH; echoes output, returns exit code
  local root="$1" bindir="$2"; shift 2
  VALIDATE_ROOT_OVERRIDE="$root" PATH="$bindir:$PATH" bash "$TARGET" "$@" 2>&1
}

echo "validate-local-db-volume.test.sh"

# --- STATIC -----------------------------------------------------------------
echo "static checks:"

make_root "$TMP/good" "$GOOD_APPHOST"
out="$(run_target "$TMP/good" "$TMP/bin_unused")"; rc=$?
[ $rc -eq 0 ] && ok "guarded AppHost.cs passes static check" \
              || bad "guarded AppHost.cs should pass (rc=$rc): $out"

make_root "$TMP/legacy" "$LEGACY_APPHOST"
out="$(run_target "$TMP/legacy" "$TMP/bin_unused")"; rc=$?
[ $rc -eq 1 ] && ok "pre-fix AppHost.cs (silent fallback) is rejected" \
              || bad "pre-fix AppHost.cs must fail (rc=$rc): $out"

make_root "$TMP/defanged" "$DEFANGED_APPHOST"
out="$(run_target "$TMP/defanged" "$TMP/bin_unused")"; rc=$?
[ $rc -eq 1 ] && ok "warn-instead-of-throw AppHost.cs is rejected" \
              || bad "defanged guard must fail (rc=$rc): $out"

out="$(VALIDATE_ROOT_OVERRIDE="$TMP/nonexistent" bash "$TARGET" 2>&1)"; rc=$?
[ $rc -eq 2 ] && ok "missing AppHost.cs reports precondition error (2)" \
              || bad "missing AppHost.cs should exit 2 (rc=$rc): $out"

# The real, shipped AppHost.cs must satisfy the guard.
REAL_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
out="$(bash "$TARGET" 2>&1)"; rc=$?
[ $rc -eq 0 ] && ok "repository AppHost.cs satisfies the guard" \
              || bad "repository AppHost.cs fails the guard (rc=$rc): $out"

# --- RUNTIME ----------------------------------------------------------------
echo "runtime checks:"

make_root "$TMP/rt" "$GOOD_APPHOST"

make_stubs "$TMP/bin_auto" "db-461cee1b" "sentencestudio.apphost-461cee1bad-db-data"
out="$(run_target "$TMP/rt" "$TMP/bin_auto" --runtime --expect-volume sentencestudio-local-crispy-barnacle-db-data)"; rc=$?
[ $rc -eq 1 ] && ok "auto-named volume (the real 2026-08-20 regression) is caught" \
              || bad "auto-named volume must fail (rc=$rc): $out"
case "$out" in *"AUTO-NAMED"*) ok "auto-named failure names the root cause" ;;
               *) bad "expected AUTO-NAMED diagnosis: $out" ;; esac

make_stubs "$TMP/bin_good" "db-461cee1b" "sentencestudio-local-crispy-barnacle-db-data"
out="$(run_target "$TMP/rt" "$TMP/bin_good" --runtime --expect-volume sentencestudio-local-crispy-barnacle-db-data)"; rc=$?
[ $rc -eq 0 ] && ok "correct named volume passes runtime check" \
              || bad "correct named volume should pass (rc=$rc): $out"

out="$(run_target "$TMP/rt" "$TMP/bin_good" --runtime --expect-volume sentencestudio-some-other-db-data)"; rc=$?
[ $rc -eq 1 ] && ok "volume mismatch is caught" \
              || bad "volume mismatch must fail (rc=$rc): $out"

make_stubs "$TMP/bin_none" "" ""
out="$(run_target "$TMP/rt" "$TMP/bin_none" --runtime)"; rc=$?
[ $rc -eq 1 ] && ok "no running postgres container is reported as failure" \
              || bad "missing container must fail (rc=$rc): $out"

# account presence: PGPASSWORD set, stub returns 0 rows => must fail
out="$(VALIDATE_ROOT_OVERRIDE="$TMP/rt" PATH="$TMP/bin_good:$PATH" PGPASSWORD=x STUB_ACCOUNT_COUNT=0 \
  bash "$TARGET" --runtime --expect-volume sentencestudio-local-crispy-barnacle-db-data \
  --expect-account dave@ortinau.com 2>&1)"; rc=$?
[ $rc -eq 1 ] && ok "missing dave@ortinau.com is caught" \
              || bad "absent account must fail (rc=$rc): $out"

out="$(VALIDATE_ROOT_OVERRIDE="$TMP/rt" PATH="$TMP/bin_good:$PATH" PGPASSWORD=x STUB_ACCOUNT_COUNT=1 \
  bash "$TARGET" --runtime --expect-volume sentencestudio-local-crispy-barnacle-db-data \
  --expect-account dave@ortinau.com 2>&1)"; rc=$?
[ $rc -eq 0 ] && ok "present dave@ortinau.com passes" \
              || bad "present account should pass (rc=$rc): $out"

echo
echo "passed: $pass   failed: $fail"
[ "$fail" -eq 0 ] || exit 1
