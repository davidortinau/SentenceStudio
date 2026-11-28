#!/bin/bash
# The SwiftUI bridge for a fix session of the feedback check (docs/admin-feedback-fix.md, "The
# SwiftUI bridge"). The native iOS app draws through the package Microsoft.SwiftUI, whose source
# is a repository of its own, github.com/davidortinau/Microsoft.SwiftUI. A session has a clone
# of it under its home, which the runner brings up to date before each session. This script says
# where it is and turns a change to it into a package this user's builds use.
#
#   scripts/session-bridge.sh where   where the clone is, what it is at, the version this
#                                     worktree pins, where that version's source is, the next
#                                     free version, the branch a pull request there goes to,
#                                     and whether this login may push there
#   scripts/session-bridge.sh test    builds the native framework and the bridge's own tests,
#                                     and runs them: the managed test program, then the Swift
#                                     side's tests (swift test); says each count, and stops
#                                     when a test of either fails
#   scripts/session-bridge.sh pack    builds the native framework, packs and verifies the version
#                                     the bridge's project file names, puts the package in
#                                     this user's package folder, where every build of this
#                                     user's finds it, and removes the view tests' build
#                                     output in this worktree, which would keep the old
#                                     native code
#   scripts/session-bridge.sh sync <folder>
#                                     for the runner: every Microsoft.SwiftUI package in the
#                                     folder (the ones the Captain packed) goes into this
#                                     user's package folder, over a copy that differs
#   scripts/session-bridge.sh package
#                                     for the runner (scripts/admin-feedback-bridge-release.sh):
#                                     builds, packs and verifies as pack does and puts the
#                                     package nowhere; it stays in the clone's artifacts folder
#
# pack packs what is committed: it refuses a clone with uncommitted changes, so the package
# that is built and proved is the source in the bridge's pull request. It also refuses a
# version that the bridge's repository has a tag for: a version that was given out is never
# packed again with other contents. Give the change the next version first (<Version> in
# src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj).
#
# The two project files that name the bridge's feed (src/SentenceStudio.Native.iOS/NuGet.Config
# and the view tests') point at the Captain's own folder, which a session cannot read. NuGet
# looks in the user's package folder before any feed, so a package put there is found. That is
# what pack and sync do, by restoring a project that only downloads it.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." 2>/dev/null && pwd)"
BRIDGE="${SESSION_BRIDGE:-$HOME/sstudio/bridge}"
BRIDGE_REPO="${SESSION_BRIDGE_REPO:-davidortinau/Microsoft.SwiftUI}"
PROJECT="$BRIDGE/src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj"
PINS="$ROOT/src/SentenceStudio.Native.iOS/SentenceStudio.Native.iOS.csproj"
VIEW_TESTS="$ROOT/tests/SentenceStudio.Native.SwiftUIViews.Tests"
PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
# Where the logs of the steps go.
LOGS="${SESSION_BRIDGE_LOGS:-${TMPDIR:-/tmp}}"

# No build server and no build nodes kept between commands. With them a pack in a session,
# after two runs of the tests, stopped for good at the bridge's first `dotnet run` (2026-11-24,
# the first session to use this); the runner's proof has built without them since it began.
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

say() { printf '%s\n' "$*" >&2; }
stop() { say "STOP: $*"; exit 1; }

# Ends a command that runs over its time, with what it started, so a step that hangs costs
# its limit and says so. (A watch by the clock: dotnet and xcodebuild do not end on an alarm.)
# $1 seconds, rest: the command. 124 when it ran out of time.
within() {
  local limit=$1 waited=0 running; shift
  # In a process group of its own, so that everything it started ends with it.
  perl -e 'setpgrp(0, 0); exec @ARGV or exit 127' "$@" & running=$!
  while kill -0 "$running" 2>/dev/null; do
    if [ "$waited" -ge "$limit" ]; then
      kill -TERM -- "-$running" 2>/dev/null; sleep 2; kill -KILL -- "-$running" 2>/dev/null
      wait "$running" 2>/dev/null; return 124
    fi
    sleep 2; waited=$((waited + 2))
  done
  wait "$running"
}
# What to say of a step that ended with $1, for a message. $2 its limit.
ended() { if [ "$1" = 124 ]; then echo "did not finish in $2 seconds and was stopped"; else echo "failed"; fi; }

have_bridge() {
  [ -f "$PROJECT" ] && git -C "$BRIDGE" rev-parse --git-dir >/dev/null 2>&1 \
    || stop "the bridge's source is not at $BRIDGE. The runner clones it there before a session; its summary says why it could not."
}

# The version a project file names. $1 the file, $2 what to look for.
version_in() {
  python3 -I - "$1" "$2" <<'PY'
import re, sys
text = open(sys.argv[1], encoding="utf-8").read()
if sys.argv[2] == "pin":
    found = re.search(r'<PackageVersion\s+Include="Microsoft\.SwiftUI"\s+Version="([^"]+)"', text)
else:
    found = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", text)
print(found.group(1) if found else "")
PY
}

# The next version nobody has: one after the newest in the tags and in the commit subjects of
# every branch the clone knows. $1 the pinned version.
next_version() {
  python3 -I - "$1" "$(git -C "$BRIDGE" tag -l 'v*')" \
    "$(git -C "$BRIDGE" log --all -E --grep='preview\.[0-9]+' --format=%s | head -300)" <<'PY'
import re, sys
numbers = [int(n) for text in sys.argv[1:] for n in re.findall(r"preview\.(\d+)", text)]
stem = re.sub(r"\d+$", "", sys.argv[1]) if re.search(r"preview\.\d+$", sys.argv[1]) else "0.1.0-preview."
print(f"{stem}{max(numbers) + 1}" if numbers else "")
PY
}

well_formed() { case "$1" in ""|*[!A-Za-z0-9.-]*) return 1 ;; esac; }

# The branch of the bridge's repository that holds a version: main when it does, or the first
# other branch there that does (while the Captain has not merged that line into main). A pull
# request goes to that branch, or it would carry every version main lacks. $1 the version.
branch_holding() {
  local tag="refs/tags/v$1" found
  git -C "$BRIDGE" rev-parse -q --verify "$tag^{commit}" >/dev/null 2>&1 || { echo main; return 0; }
  if git -C "$BRIDGE" merge-base --is-ancestor "$tag" origin/main 2>/dev/null; then echo main; return 0; fi
  found="$(git -C "$BRIDGE" for-each-ref --format='%(refname:short)' --contains "$tag" refs/remotes/origin \
            | sed -n 's#^origin/##p' | grep -v -e '^HEAD$' -e '^feedback/' | head -1)"
  echo "${found:-main}"
}

# Whether this login may push to the bridge's repository: the session's token covers it only
# when the app is installed there and its main is protected (docs/admin-feedback-check.md).
# An app's token is asked which repositories it is for: on a repository itself GitHub reports
# every permission of an app's token as false, this repository's included (the first session
# was told PUSH=no with a token that covered the bridge). A person's login is asked the usual
# way.
may_push() {
  local covered
  covered="$(gh api installation/repositories --paginate --jq '.repositories[].full_name' 2>/dev/null)"
  case "
$covered
" in *"
$BRIDGE_REPO
"*) return 0 ;; esac
  [ "$(gh api "repos/$BRIDGE_REPO" --jq '.permissions.push' 2>/dev/null)" = true ]
}

where() {
  have_bridge
  local pinned at source
  pinned="$(version_in "$PINS" pin)"
  at="$(git -C "$BRIDGE" symbolic-ref -q --short HEAD || git -C "$BRIDGE" rev-parse --short HEAD)"
  source="$(git -C "$BRIDGE" rev-parse -q --verify --short "refs/tags/v$pinned^{commit}" 2>/dev/null || echo "no tag v$pinned")"
  echo "BRIDGE=$BRIDGE"
  echo "AT=$at $(git -C "$BRIDGE" log -1 --format=%s | cut -c1-80)"
  echo "PINNED=$pinned"
  echo "SOURCE_OF_PINNED=$source"
  echo "NEXT=$(next_version "$pinned")"
  echo "PR_BASE=$(branch_holding "$pinned")"
  if may_push; then echo "PUSH=yes"; else echo "PUSH=no"; fi
  [ -z "$(git -C "$BRIDGE" status --porcelain)" ] || echo "UNCOMMITTED=yes"
}

# Puts one package file into this user's package folder, keeping the copy that was there if it
# cannot. $1 the .nupkg, $2 its version, $3 a log to add to.
install() {
  local package="$1" version="$2" log="$3" kept probe code
  kept="$PACKAGES/microsoft.swiftui/$version"
  probe="$(mktemp -d "${TMPDIR:-/tmp}/session-bridge.XXXXXXXX")" || { say "no scratch folder."; return 1; }
  mkdir -p "$probe/feed" && cp "$package" "$probe/feed/Microsoft.SwiftUI.$version.nupkg" || { rm -rf "$probe"; return 1; }
  cat > "$probe/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="bridge" value="$probe/feed" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="bridge"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
EOF
  # A project that downloads the package and nothing else.
  cat > "$probe/probe.csproj" <<EOF
<Project>
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <ItemGroup>
    <PackageDownload Include="Microsoft.SwiftUI" Version="[$version]" />
  </ItemGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
</Project>
EOF
  # The copy of this version from an earlier pack goes aside first, or the restore keeps it.
  rm -rf "$kept.before"
  [ ! -e "$kept" ] || mv "$kept" "$kept.before" || { rm -rf "$probe"; return 1; }
  ( cd "$probe" && within 180 dotnet restore probe.csproj --configfile "$probe/NuGet.config" -p:NuGetAudit=false ) >> "$log" 2>&1
  code=$?
  rm -rf "$probe"
  if [ "$code" = 0 ] && cmp -s "$kept/microsoft.swiftui.$version.nupkg" "$package"; then
    rm -rf "$kept.before"
    return 0
  fi
  rm -rf "$kept"
  [ ! -e "$kept.before" ] || mv "$kept.before" "$kept"
  return 1
}

# Builds the native framework, then packs and verifies the version the bridge's project file
# names, from what is committed. Leaves the version in VERSION, the package in PACKAGE and the
# log in PACK_LOG.
build_package() {
  have_bridge
  local version log package
  version="$(version_in "$PROJECT" version)"
  well_formed "$version" || stop "the bridge's project file names no usable version ($version)."
  if git -C "$BRIDGE" rev-parse -q --verify "refs/tags/v$version" >/dev/null 2>&1; then
    stop "version $version was already given out (tag v$version). Give your change the next version in $PROJECT first; scripts/session-bridge.sh where prints it."
  fi
  [ -z "$(git -C "$BRIDGE" status --porcelain)" ] \
    || stop "the bridge clone has uncommitted changes. Commit them first: the package must be the source that is in your branch, or what is proved is not what is merged."
  log="$LOGS/session-bridge-pack.log"
  say "building the native framework and packing $version (log: $log)"
  local code
  ( cd "$BRIDGE" && within 600 bash scripts/build-xcframework.sh ) > "$log" 2>&1; code=$?
  [ "$code" = 0 ] || { tail -15 "$log" >&2; stop "the bridge's native framework $(ended "$code" 600). The whole log is $log."; }
  ( cd "$BRIDGE" && within 420 bash scripts/pack-and-verify.sh ) >> "$log" 2>&1; code=$?
  [ "$code" = 0 ] || { tail -15 "$log" >&2; stop "the bridge's pack and verify $(ended "$code" 420). The whole log is $log."; }
  package="$BRIDGE/artifacts/Microsoft.SwiftUI.$version.nupkg"
  [ -f "$package" ] || stop "no package at $package after the pack."
  [ -z "$(git -C "$BRIDGE" status --porcelain)" ] \
    || stop "the pack changed files the bridge's repository tracks ($(git -C "$BRIDGE" status --porcelain | head -3 | tr '\n' ' ')). Look at them before going on."
  VERSION="$version"; PACKAGE="$package"; PACK_LOG="$log"
}

packed() {
  echo "VERSION=$VERSION"
  echo "COMMIT=$(git -C "$BRIDGE" rev-parse --short HEAD)"
  echo "PACKAGE=$PACKAGE"
}

pack() {
  build_package
  install "$PACKAGE" "$VERSION" "$PACK_LOG" \
    || { tail -15 "$PACK_LOG" >&2; stop "the package was packed but could not be put in $PACKAGES; the copy that was there is kept. The whole log is $PACK_LOG."; }
  # The view tests keep the native code they took from the package the first time; a pack of
  # the same version again would not replace it. Their build output goes, so the next build
  # takes it afresh. (scripts/ios-build.sh does the same for the app.)
  [ ! -d "$VIEW_TESTS" ] || rm -rf "$VIEW_TESTS/obj" "$VIEW_TESTS/bin"
  packed
  say "packed. Pin $VERSION in the two project files that name Microsoft.SwiftUI, then build."
}

bridge_tests() {
  have_bridge
  local log app
  log="$LOGS/session-bridge-test.log"
  # The tests run the Swift side too, so it is built first, from what is there now.
  local code
  ( cd "$BRIDGE" && within 600 bash scripts/build-xcframework.sh ) > "$log" 2>&1; code=$?
  [ "$code" = 0 ] || { tail -15 "$log" >&2; stop "the bridge's native framework $(ended "$code" 600). The whole log is $log."; }
  ( cd "$BRIDGE" && within 420 dotnet build tests/Microsoft.SwiftUI.Tests/Microsoft.SwiftUI.Tests.csproj -f net11.0-macos \
      -p:ValidateXcodeVersion=false -p:NuGetAudit=false -v q -nologo ) >> "$log" 2>&1; code=$?
  [ "$code" = 0 ] || { grep -E ' error ' "$log" | head -10 >&2; stop "the build of the bridge's tests $(ended "$code" 420). The whole log is $log."; }
  app="$(find "$BRIDGE/tests/Microsoft.SwiftUI.Tests/bin" -type f -path '*.app/Contents/MacOS/Microsoft.SwiftUI.Tests' 2>/dev/null | head -1)"
  [ -x "$app" ] || stop "the bridge's test program was not built. The log is $log."
  within 300 "$app" > "$log.run" 2>&1; code=$?
  cat "$log.run" >> "$log"; grep -E 'Managed tests:|FAIL|failed' "$log.run" | tail -20; rm -f "$log.run"
  [ "$code" != 124 ] || stop "the bridge's test program did not finish in 300 seconds and was stopped. Its output so far is $log."
  grep -Eq 'Managed tests: ([0-9]+)/\1 passed' "$log" || stop "the bridge's tests did not all pass. The whole output is $log."
  swift_tests "$log"
}

# The Swift side's own tests (native/MicrosoftSwiftUIShim/Tests), which the managed test program
# does not run: they draw the views and measure them, so a change to the Swift side that breaks
# a layout fails here and nowhere else. (Until 2026-11-25 no script ran them, and a session had
# to know to.) Says the count as the managed tests do, and the failures. $1 the log to add to.
swift_tests() {
  local log="$1" code total ran failed
  ( cd "$BRIDGE" && within 420 swift test --package-path native/MicrosoftSwiftUIShim ) > "$log.run" 2>&1; code=$?
  cat "$log.run" >> "$log"
  # XCTest gives the whole run's count under "All tests", and only when the run reached its
  # end: each suite says a count of its own before that, and a footer of Swift Testing follows
  # with a count of its own kind of test, of which the bridge has none.
  total="$(grep -A1 -E "^Test Suite 'All tests' (passed|failed)" "$log.run" | grep -E 'Executed [0-9]+ tests?, with' | tail -1)"
  ran="$(printf '%s\n' "$total" | sed -E 's/.*Executed ([0-9]+) tests?,.*/\1/')"
  # Tests that failed, not failures: one test can fail more than once.
  failed="$(grep -cE "^Test Case '.*' failed" "$log.run")"
  # A failed expectation, a test file that does not compile and a crash all say "error:".
  # Said from the bridge's folder and without the compiler's colours.
  grep -E "error: |^Test Case '.*' failed" "$log.run" | sed -e "s#$BRIDGE/##g" -e $'s/\x1b\\[[0-9;]*m//g' | cut -c1-400 | head -20
  rm -f "$log.run"
  [ "$code" != 124 ] || stop "the bridge's Swift tests did not finish in 420 seconds and were stopped. Their output so far is $log."
  [ -n "$total" ] || stop "the bridge's Swift tests did not run to their end: they did not build, or a test crashed. The whole output is $log."
  [ "$ran" -gt 0 ] || stop "the bridge's Swift tests ran no test. The whole output is $log."
  echo "Swift tests: $((ran - failed))/$ran passed"
  [ "$code" = 0 ] && [ "$failed" = 0 ] || stop "the bridge's Swift tests did not all pass. The whole output is $log."
}

# The packages the Captain packed are the ones a version means once it is given out. A copy of
# the same version that a session packed from its branch must not outlive that, and a version
# this user never had is put there, since it cannot read the feed the project files name.
sync() {
  local folder="${1:-}" package version log failed=0
  [ -d "$folder" ] || stop "sync needs the folder the packages are in."
  log="$LOGS/session-bridge-sync.log"; : > "$log"
  for package in "$folder"/Microsoft.SwiftUI.*.nupkg; do
    [ -f "$package" ] || continue
    version="${package##*/Microsoft.SwiftUI.}"; version="${version%.nupkg}"
    well_formed "$version" || continue
    cmp -s "$package" "$PACKAGES/microsoft.swiftui/$version/microsoft.swiftui.$version.nupkg" && continue
    if install "$package" "$version" "$log"; then echo "SYNCED=$version"; else echo "NOT_SYNCED=$version"; failed=1; fi
  done
  return "$failed"
}

case "${1:-}" in
  where) where ;;
  pack) pack ;;
  package) build_package; packed ;;
  test) bridge_tests ;;
  sync) sync "${2:-}" ;;
  *) sed -n '2,/^set -uo pipefail/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//' >&2; exit 2 ;;
esac
