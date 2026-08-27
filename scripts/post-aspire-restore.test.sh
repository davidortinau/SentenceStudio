#!/usr/bin/env bash
set -uo pipefail
# ---------------------------------------------------------------------------
# post-aspire-restore.test.sh
#
# Hermetic test for scripts/post-aspire-restore.sh.
# Injects stub curl/docker commands via environment variables so no live stack
# is required. Safe to run at any time.
# ---------------------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TARGET="$SCRIPT_DIR/post-aspire-restore.sh"

pass=0
fail=0

ok()   { echo "  ok   - $1"; pass=$((pass + 1)); }
bad()  { echo "  FAIL - $1"; fail=$((fail + 1)); }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# --- stub builders ---

# Creates a stub curl that returns a given HTTP code
make_curl_stub() {
  local http_code="$1"
  local stub="$TMP/curl"
  cat > "$stub" <<STUB
#!/usr/bin/env bash
# Stub curl: always prints the configured HTTP code on -w '%{http_code}'
echo -n "$http_code"
STUB
  chmod +x "$stub"
  echo "$stub"
}

# Creates a stub docker that returns given volume names for inspect
make_docker_stub() {
  local volumes="$1"
  local ps_names="${2:-db-abc123}"
  local stub="$TMP/docker"
  cat > "$stub" <<STUB
#!/usr/bin/env bash
if [[ "\$1" == "ps" ]]; then
  echo "$ps_names"
elif [[ "\$1" == "inspect" ]]; then
  echo "$volumes"
fi
STUB
  chmod +x "$stub"
  echo "$stub"
}

# --- Test cases ---

echo "post-aspire-restore.sh tests"
echo "---"

# Test 1: All healthy - passes
curl_stub=$(make_curl_stub "200")
docker_stub=$(make_docker_stub "my-volume ")
output=$(CURL="$curl_stub" DOCKER="$docker_stub" "$TARGET" --expected-volume my-volume 2>&1)
rc=$?
if [[ $rc -eq 0 ]]; then ok "all healthy returns exit 0"; else bad "all healthy returns exit 0 (got $rc)"; fi

# Test 2: Missing --expected-volume fails
output=$("$TARGET" 2>&1)
rc=$?
if [[ $rc -eq 1 ]] && echo "$output" | grep -q "required"; then
  ok "missing --expected-volume fails with guidance"
else
  bad "missing --expected-volume should fail (got rc=$rc)"
fi

# Test 3: API unhealthy (503)
curl_stub_webapp=$(make_curl_stub "200")
# Need a curl that returns 200 for webapp but 503 for API
curl_multi="$TMP/curl_multi"
cat > "$curl_multi" <<'STUB'
#!/usr/bin/env bash
# Returns 200 for first call (webapp), 503 for second (api/health)
if echo "$@" | grep -q "/health"; then
  echo -n "503"
else
  echo -n "200"
fi
STUB
chmod +x "$curl_multi"
docker_stub=$(make_docker_stub "my-volume ")
output=$(CURL="$curl_multi" DOCKER="$docker_stub" "$TARGET" --expected-volume my-volume 2>&1)
rc=$?
if [[ $rc -eq 1 ]]; then ok "unhealthy API returns exit 1"; else bad "unhealthy API should fail (got $rc)"; fi

# Test 4: Wrong volume mounted
curl_stub=$(make_curl_stub "200")
docker_stub=$(make_docker_stub "wrong-volume ")
output=$(CURL="$curl_stub" DOCKER="$docker_stub" "$TARGET" --expected-volume expected-volume 2>&1)
rc=$?
if [[ $rc -eq 1 ]]; then ok "wrong volume returns exit 1"; else bad "wrong volume should fail (got $rc)"; fi

# Test 5: Log file with validator error
curl_stub=$(make_curl_stub "200")
docker_stub=$(make_docker_stub "my-vol ")
log_file="$TMP/api_out"
echo "OptionsValidationException: Coach:AllowedUserProfileIds[2] is a duplicate entry." > "$log_file"
output=$(CURL="$curl_stub" DOCKER="$docker_stub" "$TARGET" --expected-volume my-vol --log-file "$log_file" 2>&1)
rc=$?
if [[ $rc -eq 1 ]]; then ok "validator error in log returns exit 1"; else bad "validator error should fail (got $rc)"; fi

# Test 6: Clean log passes
curl_stub=$(make_curl_stub "200")
docker_stub=$(make_docker_stub "my-vol ")
clean_log="$TMP/clean_out"
echo "info: Application started." > "$clean_log"
output=$(CURL="$curl_stub" DOCKER="$docker_stub" "$TARGET" --expected-volume my-vol --log-file "$clean_log" 2>&1)
rc=$?
if [[ $rc -eq 0 ]]; then ok "clean log returns exit 0"; else bad "clean log should pass (got $rc)"; fi

# Test 7: No log file provided still passes other checks
curl_stub=$(make_curl_stub "200")
docker_stub=$(make_docker_stub "ok-vol ")
output=$(CURL="$curl_stub" DOCKER="$docker_stub" "$TARGET" --expected-volume ok-vol 2>&1)
rc=$?
if [[ $rc -eq 0 ]]; then ok "no log file skips log check gracefully"; else bad "no log file should not fail (got $rc)"; fi

# --- Summary ---
echo "---"
echo "$pass passed, $fail failed"
if [[ $fail -gt 0 ]]; then exit 1; fi
exit 0
