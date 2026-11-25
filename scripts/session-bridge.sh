#!/bin/bash
# The SwiftUI bridge for a fix session of the feedback check (docs/admin-feedback-fix.md, "The
# SwiftUI bridge"). The native iOS app draws through the package Microsoft.SwiftUI, whose source
# is a repository of its own, github.com/davidortinau/Microsoft.SwiftUI. A session has a clone
# of it under its home, which the runner brings up to date before each session. This script says
# where it is and turns a change to it into a package this user's builds use.
#
#   scripts/session-bridge.sh where   where the clone is, what it is at, the version this
#                                     worktree pins, where that version's source is, and the
#                                     next free version
#   scripts/session-bridge.sh pack    builds the native framework, packs and verifies the version
#                                     the bridge's project file names, and puts the package in
#                                     this user's package folder, where every build of this
#                                     user's finds it
#   scripts/session-bridge.sh test    builds the native framework and the bridge's own tests,
#                                     and runs them
#
# pack refuses a version that the bridge's repository already has a tag for: a version that was
# given out is never packed again with other contents. Give the change the next version first
# (<Version> in src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj).
#
# The two project files that name the bridge's feed (src/SentenceStudio.Native.iOS/NuGet.Config
# and the view tests') point at the Captain's own folder, which a session cannot read. NuGet
# looks in the user's package folder before any feed, so a package put there is found. That is
# what pack does, by restoring a project that only downloads it.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BRIDGE="${SESSION_BRIDGE:-$HOME/sstudio/bridge}"
PROJECT="$BRIDGE/src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj"
PINS="$ROOT/src/SentenceStudio.Native.iOS/SentenceStudio.Native.iOS.csproj"
PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

say() { printf '%s\n' "$*" >&2; }
stop() { say "STOP: $*"; exit 1; }

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

where() {
  have_bridge
  local pinned at source next
  pinned="$(version_in "$PINS" pin)"
  at="$(git -C "$BRIDGE" symbolic-ref -q --short HEAD || git -C "$BRIDGE" rev-parse --short HEAD)"
  source="$(git -C "$BRIDGE" rev-parse -q --verify --short "refs/tags/v$pinned^{commit}" 2>/dev/null || echo "no tag v$pinned")"
  next="$(next_version "$pinned")"
  echo "BRIDGE=$BRIDGE"
  echo "AT=$at $(git -C "$BRIDGE" log -1 --format=%s | cut -c1-80)"
  echo "PINNED=$pinned"
  echo "SOURCE_OF_PINNED=$source"
  echo "NEXT=$next"
  [ -z "$(git -C "$BRIDGE" status --porcelain)" ] || echo "UNCOMMITTED=yes"
}

pack() {
  have_bridge
  local version log package kept probe
  version="$(version_in "$PROJECT" version)"
  well_formed "$version" || stop "the bridge's project file names no usable version ($version)."
  if git -C "$BRIDGE" rev-parse -q --verify "refs/tags/v$version" >/dev/null 2>&1; then
    stop "version $version was already given out (tag v$version). Give your change the next version in $PROJECT first; scripts/session-bridge.sh where prints it."
  fi
  log="${TMPDIR:-/tmp}/session-bridge-pack.log"
  say "building the native framework and packing $version (log: $log)"
  ( cd "$BRIDGE" && bash scripts/build-xcframework.sh && bash scripts/pack-and-verify.sh ) > "$log" 2>&1 \
    || { tail -15 "$log" >&2; stop "the bridge did not build, pack or verify. The whole log is $log."; }
  package="$BRIDGE/artifacts/Microsoft.SwiftUI.$version.nupkg"
  [ -f "$package" ] || stop "no package at $package after the pack."

  # Into this user's package folder. A copy of the same version from an earlier pack goes
  # first, or the build keeps using it.
  kept="$PACKAGES/microsoft.swiftui/$version"
  rm -rf "$kept"
  probe="$(mktemp -d "${TMPDIR:-/tmp}/session-bridge.XXXXXXXX")" || stop "no scratch folder."
  cat > "$probe/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="bridge" value="$BRIDGE/artifacts" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="bridge"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
EOF
  # A project that downloads the package and nothing else, with no SDK to fetch.
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
  ( cd "$probe" && dotnet restore probe.csproj --configfile "$probe/NuGet.config" -p:NuGetAudit=false ) >> "$log" 2>&1
  rm -rf "$probe"
  [ -f "$kept/microsoft.swiftui.$version.nupkg" ] \
    || { tail -15 "$log" >&2; stop "the package was packed but could not be put in $PACKAGES. The whole log is $log."; }
  cmp -s "$kept/microsoft.swiftui.$version.nupkg" "$package" \
    || stop "the package in $PACKAGES is not the one just packed."
  echo "VERSION=$version"
  echo "PACKAGE=$package"
  say "packed. Pin $version in the two project files that name Microsoft.SwiftUI, then build."
}

bridge_tests() {
  have_bridge
  local log app
  log="${TMPDIR:-/tmp}/session-bridge-test.log"
  # The tests run the Swift side too, so it is built first, from what is there now.
  ( cd "$BRIDGE" && bash scripts/build-xcframework.sh ) > "$log" 2>&1 \
    || { tail -15 "$log" >&2; stop "the bridge's native framework did not build. The whole log is $log."; }
  ( cd "$BRIDGE" && dotnet build tests/Microsoft.SwiftUI.Tests/Microsoft.SwiftUI.Tests.csproj -f net11.0-macos \
      -p:ValidateXcodeVersion=false -p:NuGetAudit=false -v q -nologo ) >> "$log" 2>&1 \
    || { grep -E ' error ' "$log" | head -10 >&2; stop "the bridge's tests did not build. The whole log is $log."; }
  app="$(find "$BRIDGE/tests/Microsoft.SwiftUI.Tests/bin" -type f -path '*.app/Contents/MacOS/Microsoft.SwiftUI.Tests' 2>/dev/null | head -1)"
  [ -x "$app" ] || stop "the bridge's test program was not built. The log is $log."
  "$app" 2>&1 | tee -a "$log" | grep -E 'Managed tests:|FAIL|failed' | tail -20
  grep -Eq 'Managed tests: ([0-9]+)/\1 passed' "$log" || stop "the bridge's tests did not all pass. The whole output is $log."
}

case "${1:-}" in
  where) where ;;
  pack) pack ;;
  test) bridge_tests ;;
  *) sed -n '2,/^set -uo pipefail/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//' >&2; exit 2 ;;
esac
