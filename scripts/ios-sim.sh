#!/bin/bash
# Build the native iOS app for the simulator, install it, launch it and confirm it runs.
#
#   scripts/ios-sim.sh [UDID] [--no-build] [--api URL] [-- extra dotnet build args]
#
# With no UDID it uses the one booted simulator (and stops if none or several are booted).
# Do not point it at a clone of a signed-in simulator: clones share one refresh token.
# Duo simulators (iOS 27.1) cannot be screenshotted; an iPhone 18 Pro on iOS 27.0 can.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UDID=""; BUILD=1; PASS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-build) BUILD=0; shift ;;
    --api|--) PASS+=("$@"); break ;;
    -*) echo "unknown option: $1" >&2; exit 64 ;;
    *) UDID="$1"; shift ;;
  esac
done

if [ -z "$UDID" ]; then
  BOOTED=$(xcrun simctl list devices booted -j | python3 -c '
import json, sys
for runtime, devices in json.load(sys.stdin)["devices"].items():
    for d in devices:
        if d.get("state") == "Booted": print(d["udid"], d["name"], sep="\t")')
  N=$(printf '%s' "$BOOTED" | grep -c . || true)
  [ "$N" = 1 ] || { echo "STOP: $N simulators booted; pass a UDID"; printf '%s\n' "$BOOTED"; exit 1; }
  UDID=$(printf '%s' "$BOOTED" | cut -f1)
  echo "simulator: $(printf '%s' "$BOOTED" | cut -f2) ($UDID)"
else
  xcrun simctl bootstatus "$UDID" -b >/dev/null
fi

APP="$ROOT/src/SentenceStudio.Native.iOS/bin/Debug/net11.0-ios/iossimulator-arm64/SentenceStudio.Native.iOS.app"
if [ $BUILD = 1 ]; then
  OUT=$("$ROOT/scripts/ios-build.sh" sim ${PASS[@]+"${PASS[@]}"}) || { echo "$OUT"; exit 1; }
  echo "$OUT"
  APP=$(echo "$OUT" | sed -n 's/^APP=//p' | tail -1)
fi
BUNDLE=$(plutil -extract CFBundleIdentifier raw "$APP/Info.plist")

xcrun simctl terminate "$UDID" "$BUNDLE" >/dev/null 2>&1 || true
xcrun simctl install "$UDID" "$APP"
PID=$(xcrun simctl launch "$UDID" "$BUNDLE" | awk '{print $NF}')
sleep 5
if xcrun simctl spawn "$UDID" launchctl list 2>/dev/null | grep -q "UIKitApplication:$BUNDLE"; then
  echo "ok:   $BUNDLE running on $UDID (pid $PID)"
else
  echo "FAIL: $BUNDLE is not running 5 s after launch; check crash logs"; exit 1
fi
