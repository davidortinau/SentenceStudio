#!/bin/bash
# Build the native iOS Release app against production and install it on one of the
# Captain's phones, keeping the app's data (install over the existing app).
#
#   scripts/ios-phone.sh Duotopia|DX24 [--yes] [--no-build]
#
# Without --yes it builds, checks the phone and stops before installing. The phones are
# production devices: an agent passes --yes only after the Captain approves the install
# in that turn. It never uninstalls, and it stops if the app is running on the phone
# (ask the Captain to close it; the script does not kill it).
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
RUNNING=$(xcrun devicectl device info processes --device "$DEVICE" 2>/dev/null \
  | grep -c 'SentenceStudio.Native.iOS' || true)
if [ "$RUNNING" != 0 ]; then
  echo "STOP: SentenceStudio is running on $PHONE; ask the Captain to close it first"; exit 1
fi
echo "ok:   $PHONE reachable; the app is not running"

if [ $YES = 0 ]; then
  echo "READY: rerun with --yes --no-build after the Captain approves installing on $PHONE"; exit 2
fi

install() { xcrun devicectl device install app --device "$DEVICE" "$APP" 2>&1 | grep -E 'App installed|error|Error' | head -4; }
if ! install | grep -q 'App installed'; then
  echo "first install attempt failed (common after deep sleep); warming the tunnel and retrying"
  xcrun devicectl device info details --device "$DEVICE" >/dev/null 2>&1 || true
  install | grep -q 'App installed' || { echo "FAIL: install failed twice"; exit 1; }
fi
echo "ok:   installed on $PHONE"

if xcrun devicectl device process launch --device "$DEVICE" "$BUNDLE" >/dev/null 2>&1; then
  sleep 6
  if xcrun devicectl device info processes --device "$DEVICE" 2>/dev/null | grep -q 'SentenceStudio.Native.iOS'; then
    echo "ok:   launched and running on $PHONE"
  else
    echo "FAIL: launched but not running after 6 s"; exit 1
  fi
else
  echo "WARN: installed, but launch failed (phone locked?); unlock it and open the app"
fi
