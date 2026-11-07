#!/bin/bash
# Build the native iOS app and check that the .app holds what was just built.
#
#   scripts/ios-build.sh sim    [--api URL] [-- extra dotnet build args]
#   scripts/ios-build.sh device [--api URL] [-- extra dotnet build args]
#
#   sim:    Debug, iossimulator-arm64, API http://localhost:5081 (the local AppHost)
#   device: Release, ios-arm64, the production API
#
# Incremental builds have shipped stale copies of two generated outputs, so this script
# moves both aside before every build (they are regenerated):
#   obj/<cfg>/net11.0-ios/<rid>/R2R                            (an old AppLib, 2026-11-06)
#   obj/<cfg>/net11.0-ios/<rid>/Microsoft.SwiftUI.resources.zip (an old bridge shim)
# After the build it checks the bundle ID, the API URL, that every SentenceStudio
# assembly in the .app is newer than the build start, that the app's shim code matches
# the package's, and for Release that no Ailoha files are present.
#
# The last line printed is APP=<path to the .app>. Exit 0 = build and checks passed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
PROD_API=https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io
PROJ=src/SentenceStudio.Native.iOS/SentenceStudio.Native.iOS.csproj
HEAD_DIR=src/SentenceStudio.Native.iOS

TARGET="${1:-}"; shift || true
case "$TARGET" in
  sim)    CFG=Debug;   RID=iossimulator-arm64; API=http://localhost:5081; SLICE=simulator ;;
  device) CFG=Release; RID=ios-arm64;          API=$PROD_API;            SLICE=device ;;
  *) echo "usage: $0 sim|device [--api URL] [-- extra dotnet build args]" >&2; exit 64 ;;
esac
EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --api) API="$2"; shift 2 ;;
    --) shift; EXTRA=("$@"); break ;;
    *) echo "unknown option: $1" >&2; exit 64 ;;
  esac
done

OBJ=$HEAD_DIR/obj/$CFG/net11.0-ios/$RID
APP=$HEAD_DIR/bin/$CFG/net11.0-ios/$RID/SentenceStudio.Native.iOS.app
ASIDE="${TMPDIR:-/tmp}/sstudio-stale-ios-outputs/$(date +%Y%m%d-%H%M%S)-$CFG-$RID"
LOG="${TMPDIR:-/tmp}/sstudio-ios-build-$CFG-$RID.log"

for stale in "$OBJ/R2R" "$OBJ/Microsoft.SwiftUI.resources.zip"; do
  if [ -e "$stale" ]; then
    mkdir -p "$ASIDE"
    mv "$stale" "$ASIDE/"
  fi
done
[ -d "$ASIDE" ] && echo "moved stale outputs to $ASIDE"

MARK=$(mktemp "${TMPDIR:-/tmp}/sstudio-ios-build-start.XXXXXX")
echo "building $CFG $RID against $API (log: $LOG)"
set +e
dotnet build "$PROJ" -f net11.0-ios -c "$CFG" -p:RuntimeIdentifier="$RID" \
  -p:ValidateXcodeVersion=false -p:NuGetAudit=false -p:NativeApiBaseUrl="$API" \
  -nologo -v:q ${EXTRA[@]+"${EXTRA[@]}"} > "$LOG" 2>&1
STATUS=$?
set -e
if [ $STATUS -ne 0 ]; then
  grep -E ' error ' "$LOG" | sort -u | head -20
  echo "FAIL: build exited $STATUS (full log: $LOG)"; rm -f "$MARK"; exit 1
fi

FAILS=0
fail() { echo "FAIL: $*"; FAILS=$((FAILS + 1)); }
ok() { echo "ok:   $*"; }

[ -d "$APP" ] || { echo "FAIL: no app at $APP"; rm -f "$MARK"; exit 1; }

BUNDLE=$(plutil -extract CFBundleIdentifier raw "$APP/Info.plist")
ok "bundle $BUNDLE"

BUILT_API=$(sed -n -E 's/.*"SentenceStudio[^"]*ApiBaseUrl", "([^"]*)".*/\1/p' \
  "$OBJ"/SentenceStudio.Native.iOS.AssemblyInfo.cs | head -1)
if [ "${BUILT_API%/}" = "${API%/}" ]; then ok "API $BUILT_API"; else fail "API is '$BUILT_API', expected $API"; fi

OLD=$(find "$APP" -maxdepth 1 -name 'SentenceStudio*.dll' ! -newer "$MARK")
COUNT=$(find "$APP" -maxdepth 1 -name 'SentenceStudio*.dll' | wc -l | tr -d ' ')
if [ -z "$OLD" ] && [ "$COUNT" -gt 0 ]; then
  ok "$COUNT SentenceStudio assemblies are from this build"
else
  fail "assemblies older than this build: $(echo $OLD | xargs -n1 basename 2>/dev/null | tr '\n' ' ')"
fi
rm -f "$MARK"

APP_SHIM="$APP/Frameworks/MicrosoftSwiftUIShim.framework/MicrosoftSwiftUIShim"
if [ "$SLICE" = simulator ]; then SLICE_DIR='*simulator*'; else SLICE_DIR='ios-arm64'; fi
PKG_SHIM=$(find "$OBJ/Microsoft.SwiftUI.resources.zip" -path "*/$SLICE_DIR/*" \
  -name MicrosoftSwiftUIShim -type f 2>/dev/null | head -1)
text_md5() { otool -arch arm64 -s __TEXT __text "$1" | tail -n +3 | md5; }
if [ -n "$PKG_SHIM" ] && [ -f "$APP_SHIM" ] && [ "$(text_md5 "$APP_SHIM")" = "$(text_md5 "$PKG_SHIM")" ]; then
  ok "SwiftUI shim matches the package"
else
  fail "SwiftUI shim in the app differs from the package (or one is missing)"
fi

if [ "$CFG" = Release ]; then
  AILOHA=$(find "$APP" -iname '*ailoha*' | wc -l | tr -d ' ')
  if [ "$AILOHA" = 0 ]; then ok "no Ailoha files"; else fail "$AILOHA Ailoha files in a Release app"; fi
  PROFILE=$(security cms -D -i "$APP/embedded.mobileprovision" 2>/dev/null | plutil -extract Name raw - 2>/dev/null || true)
  if [ -n "$PROFILE" ]; then ok "profile $PROFILE"; else fail "no provisioning profile"; fi
fi

if [ $FAILS -gt 0 ]; then echo "FAIL: $FAILS check(s) failed"; exit 1; fi
echo "APP=$ROOT/$APP"
