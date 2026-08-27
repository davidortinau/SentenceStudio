#!/usr/bin/env bash
#
# clone-local-db-volume.test.sh
#
# Guard tests for scripts/clone-local-db-volume.sh.
#
# These exercise the refusal paths — the ones that stand between a routine clone and a destroyed
# local database — using a stub `docker` on PATH. No real volume, container, or image is touched,
# so the suite is safe to run on a machine with a live stack.
#
# Run from anywhere:  scripts/clone-local-db-volume.test.sh
#
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUBJECT="$SCRIPT_DIR/clone-local-db-volume.sh"
STUB_DIR="$(mktemp -d -t clone-vol-test)"
FAILURES=0

cleanup() { rm -rf "$STUB_DIR"; }
trap cleanup EXIT

# A stub docker driven by two environment variables:
#   STUB_VOLUMES  space-separated volume names that "exist"
#   STUB_RUNNING  space-separated container names "running" on the queried volume
# Any command that would mutate state records itself to $STUB_DIR/calls.log and succeeds.
cat > "$STUB_DIR/docker" <<'STUB'
#!/usr/bin/env bash
echo "$*" >> "$STUB_CALLS"
case "$1" in
    volume)
        case "$2" in
            inspect)
                for v in ${STUB_VOLUMES:-}; do
                    [[ "$v" == "$3" ]] && exit 0
                done
                exit 1
                ;;
            create) exit 0 ;;
        esac
        ;;
    ps)
        for name in ${STUB_RUNNING:-}; do echo "$name"; done
        exit 0
        ;;
esac
exit 0
STUB
chmod +x "$STUB_DIR/docker"

export PATH="$STUB_DIR:$PATH"
export STUB_CALLS="$STUB_DIR/calls.log"

# Runs the subject and asserts on exit status plus an expected fragment of its output.
expect() {
    local name="$1" expected_status="$2" expected_text="$3"; shift 3
    : > "$STUB_CALLS"
    local output status
    output="$("$@" 2>&1)"
    status=$?

    if [[ "$expected_status" == "zero" && $status -ne 0 ]]; then
        echo "FAIL  $name: expected success, got exit $status"
        echo "$output" | sed 's/^/      /'
        FAILURES=$((FAILURES + 1))
        return
    fi
    if [[ "$expected_status" == "nonzero" && $status -eq 0 ]]; then
        echo "FAIL  $name: expected failure, got exit 0"
        echo "$output" | sed 's/^/      /'
        FAILURES=$((FAILURES + 1))
        return
    fi
    if [[ -n "$expected_text" && "$output" != *"$expected_text"* ]]; then
        echo "FAIL  $name: output did not contain '$expected_text'"
        echo "$output" | sed 's/^/      /'
        FAILURES=$((FAILURES + 1))
        return
    fi
    echo "pass  $name"
}

assert_no_call() {
    local name="$1" fragment="$2"
    if grep -q -- "$fragment" "$STUB_CALLS" 2>/dev/null; then
        echo "FAIL  $name: docker was called with '$fragment'"
        FAILURES=$((FAILURES + 1))
        return
    fi
    echo "pass  $name"
}

echo "Guard tests for clone-local-db-volume.sh"
echo

STUB_VOLUMES="src-vol existing-dest" STUB_RUNNING="" \
    expect "requires --source" nonzero "--source is required" \
    "$SUBJECT" --destination new-vol

STUB_VOLUMES="src-vol" STUB_RUNNING="" \
    expect "requires --destination" nonzero "--destination is required" \
    "$SUBJECT" --source src-vol

STUB_VOLUMES="src-vol" STUB_RUNNING="" \
    expect "refuses source == destination" nonzero "must differ" \
    "$SUBJECT" --source src-vol --destination src-vol

STUB_VOLUMES="" STUB_RUNNING="" \
    expect "refuses a missing source" nonzero "does not exist" \
    "$SUBJECT" --source ghost-vol --destination new-vol

STUB_VOLUMES="src-vol existing-dest" STUB_RUNNING="" \
    expect "refuses an existing destination" nonzero "already exists" \
    "$SUBJECT" --source src-vol --destination existing-dest
assert_no_call "does not create over an existing destination" "volume create existing-dest"

STUB_VOLUMES="src-vol" STUB_RUNNING="db-live" \
    expect "refuses a file copy while the source is running" nonzero "torn page" \
    "$SUBJECT" --source src-vol --destination new-vol --mode copy
assert_no_call "does not create a volume when the copy is refused" "volume create new-vol"

STUB_VOLUMES="src-vol" STUB_RUNNING="" \
    expect "refuses dump mode with no running source" nonzero "needs a running PostgreSQL" \
    "$SUBJECT" --source src-vol --destination new-vol --mode dump

STUB_VOLUMES="src-vol" STUB_RUNNING="" \
    expect "auto mode picks copy for a stopped source" zero "Mode .................. copy" \
    "$SUBJECT" --source src-vol --destination new-vol --dry-run

STUB_VOLUMES="src-vol" STUB_RUNNING="db-live" \
    expect "auto mode picks dump for a running source" zero "Mode .................. dump" \
    "$SUBJECT" --source src-vol --destination new-vol --dry-run

STUB_VOLUMES="src-vol" STUB_RUNNING="" \
    expect "dry run reports it created nothing" zero "nothing was created" \
    "$SUBJECT" --source src-vol --destination new-vol --dry-run
assert_no_call "dry run creates no volume" "volume create"

STUB_VOLUMES="src-vol" STUB_RUNNING="" \
    expect "rejects an unknown mode" nonzero "--mode must be" \
    "$SUBJECT" --source src-vol --destination new-vol --mode wipe

echo
if [[ $FAILURES -eq 0 ]]; then
    echo "All guard tests passed."
    exit 0
fi
echo "$FAILURES guard test(s) failed."
exit 1
