#!/bin/bash
# The native iOS app on a simulator of your own, against a stack of your own, with the Ailoha
# agent in it so it can be driven by control id. Made for a fix session of the feedback check
# (docs/admin-feedback-fix.md, "Running the app"); it works for anyone whose simulators and
# Ailoha files are where it looks.
#
#   scripts/session-sim.sh up [--api URL] [--no-build]
#       Boots the simulator (making it the first time, with accessibility on), builds the app
#       with the Ailoha agent against the API, installs and launches it, and prints
#       UDID=<simulator> and AILOHA_AGENT_PORT=<the app's agent>. The API is this worktree's
#       session stack (scripts/session-stack.sh up) unless --api names another. The app starts
#       signed out and empty every time: an earlier run's app and the simulator's keychain are
#       removed first, because a sign-in kept from another stack is refused by this one (401)
#       and the app then shows a signed-in screen that cannot load anything.
#   scripts/session-sim.sh signin
#       Signs in as e2e@test.local (docs/local-dev-test-accounts.md) and starts the app again,
#       which drops the system's "Save Password?" sheet. Prints the agent's port again: it can
#       change when the app starts again.
#   scripts/session-sim.sh port                 prints AILOHA_AGENT_PORT=<port> of the running app
#   scripts/session-sim.sh screenshot <file>    a PNG of the simulator's screen
#   scripts/session-sim.sh down                 shuts the simulator down
#
# Drive the app with `AILOHA_AGENT_PORT=<port> ailoha ios ui tree|tap|fill ...`. ALWAYS give the
# port this script printed: other apps on this Mac have agents on the neighbouring ports, they
# are not yours, and a command without the port can land on one of them.
#
# It never uses the API on port 5081, which is the Captain's own stack. The only simulator it
# makes, resets or shuts down is the one named SESSION_SIM_NAME ("Session iPad").
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
AILOHA_HOME="${SESSION_AILOHA_HOME:-/Users/Shared/sstudio-feedback/ailoha}"
AGENT="$AILOHA_HOME/AilohaAgent.xcframework"
AILOHA="$AILOHA_HOME/bin/ailoha"
NAME="${SESSION_SIM_NAME:-Session iPad}"
TYPE="${SESSION_SIM_TYPE:-iPad Pro 11-inch (M5)}"
BUNDLE=com.simplyprofound.sentencestudio.native.debug
HEAD_DIR="$ROOT/src/SentenceStudio.Native.iOS"
SHIM="$HEAD_DIR/obj/ailoha-debug/AilohaDebugShim.xcframework"

say() { printf '%s\n' "$*" >&2; }
stop() { say "STOP: $*"; exit 1; }

# The simulator's UDID, or nothing when it has not been made.
udid() {
  xcrun simctl list devices -j 2>/dev/null | python3 -c '
import json, sys
name = sys.argv[1]
for devices in json.load(sys.stdin).get("devices", {}).values():
    for device in devices:
        if device.get("name") == name and device.get("isAvailable", True):
            print(device["udid"])
            sys.exit(0)' "$NAME"
}

# Makes the simulator once. SwiftUI builds its accessibility elements only when accessibility
# is on, and without them Ailoha sees text fields and nothing else; the setting is read at boot.
make() {
  local made
  # simctl says which runtime it took on standard error; the UDID is its only standard output.
  made="$(xcrun simctl create "$NAME" "$TYPE" 2>/dev/null | tail -1)"
  case "$made" in ????????-????-????-????-????????????) ;; *) stop "the simulator \"$NAME\" ($TYPE) could not be made." ;; esac
  say "made the simulator \"$NAME\" ($made); its first boot takes about two minutes"
  xcrun simctl boot "$made" >/dev/null 2>&1
  xcrun simctl bootstatus "$made" -b >/dev/null 2>&1
  xcrun simctl spawn "$made" defaults write com.apple.Accessibility AccessibilityEnabled -bool true
  xcrun simctl spawn "$made" defaults write com.apple.Accessibility ApplicationAccessibilityEnabled -bool true
  xcrun simctl shutdown "$made" >/dev/null 2>&1
  printf '%s\n' "$made"
}

need_device() {
  UDID="$(udid)"
  [ -n "$UDID" ] || stop "there is no simulator \"$NAME\": run $0 up first."
}

# The port the running app's agent listens on, read from the app's own process so that it is
# never another app's. Waits up to $1 seconds for it.
agent_port() {
  local waited=0 pid port
  while [ "$waited" -lt "${1:-30}" ]; do
    pid="$(xcrun simctl spawn "$UDID" launchctl list 2>/dev/null | awk -v b="UIKitApplication:$BUNDLE" 'index($3, b) == 1 {print $1}' | head -1)"
    case "$pid" in ""|*[!0-9]*) ;; *)
      port="$(lsof -nP -a -p "$pid" -iTCP -sTCP:LISTEN 2>/dev/null | awk 'NR > 1 {sub(/.*:/, "", $9); print $9}' | head -1)"
      case "$port" in ""|*[!0-9]*) ;; *) printf '%s\n' "$port"; return 0 ;; esac ;;
    esac
    sleep 1; waited=$((waited + 1))
  done
  return 1
}

report() {
  local port
  port="$(agent_port 30)" || stop "the app is not running with an Ailoha agent on \"$NAME\"."
  printf 'UDID=%s\nAILOHA_AGENT_PORT=%s\n' "$UDID" "$port"
}

up() {
  local api="" build=yes
  while [ $# -gt 0 ]; do
    case "$1" in
      --api) api="${2:-}"; shift 2 ;;
      --no-build) build=no; shift ;;
      *) stop "unknown option: $1" ;;
    esac
  done
  [ -n "$api" ] || api="$("$ROOT/scripts/session-stack.sh" urls 2>/dev/null | sed -n 's/^API=//p')"
  [ -n "$api" ] || stop "no API: start a stack with scripts/session-stack.sh up, or pass --api."
  case "$api" in *:5081|*:5081/*) stop "$api is the Captain's stack; this never uses it." ;; esac
  [ -f "$AGENT/Info.plist" ] && [ -x "$AILOHA" ] || stop "the Ailoha agent and command are not in $AILOHA_HOME."

  UDID="$(udid)"
  [ -n "$UDID" ] || UDID="$(make)" || exit 1
  xcrun simctl boot "$UDID" >/dev/null 2>&1
  xcrun simctl bootstatus "$UDID" -b >/dev/null 2>&1 || stop "the simulator \"$NAME\" did not boot."

  if [ "$build" = yes ]; then
    if [ ! -f "$SHIM/Info.plist" ]; then
      bash "$HEAD_DIR/scripts/build-ailoha-debug.sh" "$AGENT" >/dev/null 2>&1 \
        || stop "the Debug Ailoha shim did not build (src/SentenceStudio.Native.iOS/scripts/build-ailoha-debug.sh)."
    fi
    # Nothing of an earlier run: its app, its data and the tokens it kept in the keychain.
    xcrun simctl terminate "$UDID" "$BUNDLE" >/dev/null 2>&1
    xcrun simctl uninstall "$UDID" "$BUNDLE" >/dev/null 2>&1
    xcrun simctl keychain "$UDID" reset >/dev/null 2>&1
    "$ROOT/scripts/ios-sim.sh" "$UDID" --api "$api" -- \
        "-p:AilohaAgentXCFramework=$AGENT" "-p:AilohaDebugShimXCFramework=$SHIM" >&2 \
      || stop "the app did not build or start (scripts/ios-sim.sh)."
  else
    xcrun simctl launch "$UDID" "$BUNDLE" >/dev/null 2>&1 || stop "the app is not installed on \"$NAME\"; run without --no-build."
  fi
  report
}

signin() {
  need_device
  local port password waited=0
  port="$(agent_port 30)" || stop "the app is not running with an Ailoha agent on \"$NAME\"; run $0 up first."
  password="$(sed -n 's/.*`e2e@test.local` | `\([^`]*\)`.*/\1/p' "$ROOT/docs/local-dev-test-accounts.md" | head -1)"
  [ -n "$password" ] || stop "the test account is not in docs/local-dev-test-accounts.md."
  export AILOHA_AGENT_PORT="$port"
  # The form takes a moment to appear after the app starts.
  while ! "$AILOHA" ios ui tree --no-json 2>/dev/null | grep -q 'native\.auth\.submit'; do
    waited=$((waited + 1))
    [ "$waited" -lt 15 ] || stop "the app is not on its sign-in screen. If it shows a signed-in screen, that sign-in is from an earlier run: run $0 up again (without --no-build), which starts the app signed out."
    sleep 1
  done
  waited=0
  "$AILOHA" ios ui fill tid:native.auth.email e2e@test.local >/dev/null 2>&1 \
    && "$AILOHA" ios ui fill tid:native.auth.password "$password" >/dev/null 2>&1 \
    && "$AILOHA" ios ui tap tid:native.auth.submit >/dev/null 2>&1 \
    || stop "the sign-in form could not be filled."
  # Signed in when the form is gone.
  while "$AILOHA" ios ui tree --no-json 2>/dev/null | grep -q 'native\.auth\.submit'; do
    waited=$((waited + 1))
    [ "$waited" -lt 30 ] || stop "still on the sign-in screen after 30 s: the stack refused the sign-in, or is not up."
    sleep 1
  done
  # The system asks to save the password in a sheet Ailoha cannot reach; a fresh start has none.
  xcrun simctl terminate "$UDID" "$BUNDLE" >/dev/null 2>&1
  xcrun simctl launch "$UDID" "$BUNDLE" >/dev/null 2>&1 || stop "the app did not start again."
  sleep 3
  say "signed in as e2e@test.local"
  report
}

case "${1:-}" in
  up) shift; up "$@" ;;
  signin) signin ;;
  port) need_device; report ;;
  screenshot)
    [ -n "${2:-}" ] || stop "name the file for the screenshot."
    need_device
    xcrun simctl io "$UDID" screenshot "$2" >/dev/null 2>&1 || stop "no screenshot: is the simulator booted?"
    say "wrote $2" ;;
  down) need_device; xcrun simctl shutdown "$UDID" >/dev/null 2>&1; say "shut down" ;;
  *) sed -n '2,/^set -uo pipefail/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//' >&2; exit 2 ;;
esac
