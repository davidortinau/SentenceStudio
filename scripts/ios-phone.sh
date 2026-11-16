#!/bin/bash
# Build the native iOS Release app against production and install it on one of the
# Captain's phones, keeping the app's data (install over the existing app).
#
#   scripts/ios-phone.sh Duotopia|DX24 [--yes] [--no-build]
#
# Without --yes it builds, checks the phone and stops before installing. The phones are
# production devices: an agent passes --yes only after the Captain approves the install
# in that turn. It never uninstalls. If SentenceStudio is running on the phone, --yes ends
# it (SIGTERM, then SIGKILL if it stays listed) and installs: the Captain's approval of the
# install covers that, so do not ask him to close the app.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUNDLE=com.simplyprofound.sentencestudio.native.preview
PHONE="${1:-}"; shift || true
case "$PHONE" in
  Duotopia) DEVICE=00008160-001A2D2A0C60000A ;;
  DX24)     DEVICE=CF4F94E3-A1C9-5617-A089-9ABB0110A09F ;;
  *) echo "usage: $0 Duotopia|DX24 [--yes] [--no-build]" >&2; exit 64 ;;
esac
YES=0; BUILD=1
for arg in "$@"; do
  case "$arg" in
    --yes) YES=1 ;;
    --no-build) BUILD=0 ;;
    *) echo "unknown option: $arg" >&2; exit 64 ;;
  esac
done

APP="$ROOT/src/SentenceStudio.Native.iOS/bin/Release/net11.0-ios/ios-arm64/SentenceStudio.Native.iOS.app"
if [ $BUILD = 1 ]; then
  OUT=$("$ROOT/scripts/ios-build.sh" device) || { echo "$OUT"; exit 1; }
  echo "$OUT"
  APP=$(echo "$OUT" | sed -n 's/^APP=//p' | tail -1)
fi
[ "$(plutil -extract CFBundleIdentifier raw "$APP/Info.plist")" = "$BUNDLE" ] \
  || { echo "FAIL: $APP is not $BUNDLE"; exit 1; }
echo "app: $APP (built $(stat -f '%Sm' "$APP/SentenceStudio.Native.iOS.dll"), commit $(git -C "$ROOT" rev-parse --short HEAD))"

# A short query first: it wakes the tunnel that the longer install needs.
xcrun devicectl device info details --device "$DEVICE" >/dev/null 2>&1 \
  || { echo "STOP: $PHONE is not reachable; wake and unlock it, then rerun"; exit 1; }
# The first number on a matching row of the process table is the pid.
app_pids() {
  xcrun devicectl device info processes --device "$DEVICE" 2>/dev/null \
    | awk '/SentenceStudio\.Native\.iOS/ { for (i = 1; i <= NF; i++) if ($i ~ /^[0-9]+$/) { print $i; break } }' || true
}
PIDS=$(app_pids)
if [ -z "$PIDS" ]; then
  echo "ok:   $PHONE reachable; the app is not running"
else
  echo "ok:   $PHONE reachable; the app is running (--yes ends it before installing)"
fi

if [ $YES = 0 ]; then
  echo "READY: rerun with --yes --no-build after the Captain approves installing on $PHONE"; exit 2
fi

if [ -n "$PIDS" ]; then
  for pid in $PIDS; do
    xcrun devicectl device process terminate --device "$DEVICE" --pid "$pid" >/dev/null 2>&1 || true
  done
  sleep 2
  # A suspended app can ignore SIGTERM and stay listed (2026-11-10); SIGKILL then ends it.
  for pid in $(app_pids); do
    xcrun devicectl device process signal --device "$DEVICE" --pid "$pid" --signal SIGKILL >/dev/null 2>&1 || true
  done
  sleep 1
  [ -z "$(app_pids)" ] || { echo "FAIL: could not end the running app on $PHONE"; exit 1; }
  echo "ok:   ended the running app"
fi

# Whole outputs are read before matching: under pipefail, grep -q exits at its first match
# and the writer's SIGPIPE made these checks fail (or retry) when they had passed.
install() { xcrun devicectl device install app --device "$DEVICE" "$APP" 2>&1 || true; }
RESULT=$(install)
if [[ "$RESULT" != *"App installed"* ]]; then
  echo "$RESULT" | grep -E 'error|Error' | head -4 || true
  echo "first install attempt failed (common after deep sleep); warming the tunnel and retrying"
  xcrun devicectl device info details --device "$DEVICE" >/dev/null 2>&1 || true
  RESULT=$(install)
  [[ "$RESULT" == *"App installed"* ]] \
    || { echo "$RESULT" | grep -E 'error|Error' | head -4 || true; echo "FAIL: install failed twice"; exit 1; }
fi
echo "ok:   installed on $PHONE"

if xcrun devicectl device process launch --device "$DEVICE" "$BUNDLE" >/dev/null 2>&1; then
  sleep 6
  PROCESSES=$(xcrun devicectl device info processes --device "$DEVICE" 2>/dev/null || true)
  if [[ "$PROCESSES" == *SentenceStudio.Native.iOS* ]]; then
    echo "ok:   launched and running on $PHONE"
  else
    echo "FAIL: launched but not running after 6 s"; exit 1
  fi
else
  echo "WARN: installed, but launch failed (phone locked?); unlock it and open the app"
fi
