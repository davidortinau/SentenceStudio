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
#
# The install log (docs/app-install-log.md): what each phone runs and since when. Once the
# install has succeeded the script appends one line (the time in UTC, the commit built and
# its subject, the phone) and does not commit it: commit and push it, because a reader finds
# it on main. The line's commit is the one this script built: the build keeps it in this
# checkout's git directory, so the rerun with --no-build names it even after a pull. An app
# built another way is recorded as this checkout's HEAD, and a note says so.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUNDLE=com.simplyprofound.sentencestudio.native.preview
LOG=docs/app-install-log.md
# In this checkout's git directory: "<commit> <clean|changed>" for the app this script last
# built here. Its own time says when: an app with a newer file was built again another way.
BUILT=sstudio-phone-built
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

# Whether the build takes in tracked changes that no commit holds. The two logs the scripts
# write do not count: they are not in the app.
tree_state() {
  if git -C "$ROOT" diff --quiet HEAD -- . ':(exclude)docs/app-install-log.md' ':(exclude)docs/deploy-log.md' 2>/dev/null; then
    echo clean
  else
    echo changed
  fi
}

APP="$ROOT/src/SentenceStudio.Native.iOS/bin/Release/net11.0-ios/ios-arm64/SentenceStudio.Native.iOS.app"
MARK=$(git -C "$ROOT" rev-parse --path-format=absolute --git-path "$BUILT" 2>/dev/null) || MARK=
if [ $BUILD = 1 ]; then
  # The commit is read before the build and kept only when the build passed: a build that
  # failed leaves no commit to give to whatever app is still in the output folder.
  [ -z "$MARK" ] || rm -f "$MARK"
  BUILT_COMMIT=$(git -C "$ROOT" rev-parse HEAD 2>/dev/null) || BUILT_COMMIT=
  BUILT_STATE=$(tree_state)
  OUT=$("$ROOT/scripts/ios-build.sh" device) || { echo "$OUT"; exit 1; }
  echo "$OUT"
  APP=$(echo "$OUT" | sed -n 's/^APP=//p' | tail -1)
  if [ -n "$MARK" ] && [ -n "$BUILT_COMMIT" ]; then
    echo "$BUILT_COMMIT $BUILT_STATE" > "$MARK" || rm -f "$MARK"
  fi
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

# One line in docs/app-install-log.md for this install: the time now (UTC), the commit the
# app was built from and its subject, and the phone. Nothing is committed.
#
# The commit is the one this script built when no file of the app is newer than the mark the
# build left. Otherwise (an app built another way, or built again since) it is this
# checkout's HEAD, which nothing here can check against the app: the line is written and a
# note says so.
#
# It is called on the left of ||, where bash does not apply set -e inside the function: every
# step that can fail returns its failure by hand, and "recorded" is printed only once the
# line is in the log.
record_install() {
  local log="$ROOT/$LOG" commit= state= known= subject when line tmp
  [ -f "$log" ] || { echo "FAIL: $LOG is missing; this install is not recorded"; return 1; }
  if [ -n "$MARK" ] && [ -f "$MARK" ] && read -r commit state < "$MARK" \
     && git -C "$ROOT" cat-file -e "$commit^{commit}" 2>/dev/null \
     && [ -z "$(find "$APP" -newer "$MARK" -print -quit 2>/dev/null)" ]; then
    known=yes
  else
    commit=$(git -C "$ROOT" rev-parse HEAD 2>/dev/null) || commit=
    state=$(tree_state)
  fi
  [ -n "$commit" ] && subject=$(git -C "$ROOT" log -1 --format=%s "$commit" 2>/dev/null) \
    || { echo "FAIL: git could not read this checkout's commit; this install is not recorded in $LOG"; return 1; }
  when=$(date -u '+%Y-%m-%d %H:%M UTC') && [ -n "$when" ] \
    || { echo "FAIL: date gave no time; this install is not recorded in $LOG"; return 1; }
  line="- $when \`$commit\` $subject (phone \`$PHONE\`"
  [ "$state" = clean ] || line="$line; built with uncommitted changes"
  line="$line)"
  # The new log is written beside the old one and moved into its place only when it ends with
  # the whole line, so a write that fails leaves the log as it was, never with half a line.
  # The log may have lost its final newline to an editor; a line is never joined to the last.
  if [ -w "$log" ] && tmp=$(mktemp "$log.XXXXXX"); then
    if cp -p "$log" "$tmp" \
       && { [ -z "$(tail -c 1 "$tmp")" ] || echo >> "$tmp"; } \
       && echo "$line" >> "$tmp" \
       && [ "$(tail -n 1 "$tmp")" = "$line" ] \
       && mv -f "$tmp" "$log"; then
      echo "ok:   recorded in $LOG (not committed; commit and push it):"
      echo "      $line"
      if [ -z "$known" ]; then
        echo "note: this script did not build the app it installed (it was built another way, or built"
        echo "      again since), so the line's commit is this checkout's HEAD, which nothing here can"
        echo "      check. If the app was built from another commit, correct the line before committing it."
      fi
      return 0
    fi
    rm -f "$tmp"
  fi
  echo "FAIL: could not write to $LOG; this install is not recorded. The log is as it was."
  echo "      Add the line by hand: $line"
  return 1
}
# The app is on the phone from here on, so the install is recorded before the launch: a
# launch that fails does not take the build off the phone.
RECORDED=1
record_install || RECORDED=0

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
if [ $RECORDED = 0 ]; then
  echo "FAIL: the app is installed on $PHONE, but the install is not in $LOG (see above)"; exit 1
fi
