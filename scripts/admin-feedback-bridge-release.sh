#!/bin/bash
# The SwiftUI bridge's release, on the runner's side of the feedback check
# (docs/admin-feedback-check.md, "The SwiftUI bridge"). A fix session changes the bridge on a
# branch and opens a pull request in davidortinau/Microsoft.SwiftUI. When the Captain merges it,
# the new version exists as source only. This script makes it a package: it packs the version
# that the bridge's main names, from a clean copy of main, runs the bridge's own tests, puts
# the package in the Captain's feed and makes the release (and so the tag) that says the
# version was given out. Before 2026-11-25 the Captain did that by hand after each merge.
#
#   scripts/admin-feedback-bridge-release.sh due       says whether main holds a version to
#                                                      pack: prints "<version> <commit>" when
#                                                      it does, and nothing when it does not
#   scripts/admin-feedback-bridge-release.sh release   packs it, when there is one, and prints
#                                                      one line that begins "BRIDGE:"
#
# The runner calls both at the start of every pass (admin-feedback-check.sh, bridge_release).
# It runs as the Captain's user, never as the sessions': it runs the bridge's build scripts,
# and it writes to his feed and to the bridge's repository as him. So it packs only what is on
# the bridge's main, and only while a ruleset keeps everyone but the repository's admins from
# updating main (an active ruleset with "restrict updates" that only the repository's admins
# may bypass): then what is on main is what the Captain merged. The sessions' login can open a
# pull request there and cannot merge one. It can make branches and tags, so neither is taken
# on trust: main is read by its exact name, a tag counts only where it is on main, and the
# release's tag is read back.
#
# What it decides, from the commit main is at:
#   - the version in the project file has a tag on main: nothing to pack. If main's code (src,
#     native) has changed since that tag, it says so: a change reaches the app only under a
#     new version. If the feed has no package of that version, it says that too.
#   - the version has no tag: it is packed. A failure is tried again after an hour, three times
#     in all for one commit; after that it waits for main to change, or for the file
#     state/bridge-release-seen to be removed.
# A package of that version in the feed that this script did not put there is never replaced:
# the release stops and says so. (Two packs of one commit are not the same bytes, so what it
# put there itself is known by the record in state/bridge-release-fed, and a later try that
# finds it makes the release from it without building again.)
#
# It looks at main with one `git ls-remote` and does nothing more while main is where it was.
# What it last decided is in state/bridge-release-note, which the status issue shows; the logs
# of the last release are in bridge-release/ (release.log, and the builder's own beside it).
# Both are under ~/sstudio-feedback-check.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${FEEDBACK_CHECK_DIR:-$HOME/sstudio-feedback-check}"
BRIDGE_REPO="${FEEDBACK_BRIDGE_REPO:-davidortinau/Microsoft.SwiftUI}"
BRIDGE_URL="${FEEDBACK_BRIDGE_URL:-https://github.com/$BRIDGE_REPO.git}"
# The folder the app's project files name as the bridge's feed.
FEED="${FEEDBACK_BRIDGE_FEED:-$HOME/work/Microsoft.SwiftUI/artifacts}"
# What builds, tests and packs a clone of the bridge: the helper the sessions use, so a
# release is built the way the change was proved.
BUILDER="${FEEDBACK_BRIDGE_BUILDER:-$ROOT/scripts/session-bridge.sh}"
PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
WORK="$OUT/bridge-release"
SEEN="$OUT/state/bridge-release-seen"
NOTE="$OUT/state/bridge-release-note"
# The package this script put in the feed: main's commit, the version and the file's SHA-256 on
# the first line, what the tests said on the second.
FED="$OUT/state/bridge-release-fed"
LOG="$WORK/release.log"
TRIES=3
RETRY_AFTER="${FEEDBACK_BRIDGE_RETRY_AFTER:-3600}"
case "$RETRY_AFTER" in ""|*[!0-9]*) RETRY_AFTER=3600 ;; esac
# launchd gives the job a PATH without dotnet.
export PATH="$PATH:/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin"
# And no TMPDIR, so NuGet would keep its locks in /tmp/NuGetScratch, which belongs to whichever
# user built first on this Mac: the sessions' user, as a rule, and then no restore of the
# Captain's can take a lock there (the first trial of this script ended that way). This
# user's own temporary folder instead.
[ -n "${TMPDIR:-}" ] || { TMPDIR="$(getconf DARWIN_USER_TEMP_DIR 2>/dev/null)"; [ -d "$TMPDIR" ] && export TMPDIR || unset TMPDIR; }

say() { printf '%s\n' "$*" >&2; }
now() { date -u '+%Y-%m-%d at %H:%M UTC'; }
limited() { local seconds=$1; shift; perl -e 'alarm shift; exec @ARGV or exit 127' "$seconds" "$@"; }
sgit() { git -C "$WORK/source" "$@"; }
well_formed() { case "$1" in ""|*[!A-Za-z0-9.-]*) return 1 ;; esac; }

# What the status issue says of the bridge, until the next decision. $1 the state, on the
# file's first line (packing, released, nothing or failed: while it is packing the status does
# not say the check is idle), $2 what to say.
note() { printf '%s\n%s\n' "$1" "$2" > "$NOTE.new" && mv "$NOTE.new" "$NOTE"; }
# The commit that was decided on, which try that was, when, and how it ended (released,
# nothing or failed).
seen() { printf '%s %s %s %s\n' "$SHA" "$TRY" "$(date +%s)" "$1" > "$SEEN.new" && mv "$SEEN.new" "$SEEN"; }

# Whether main holds a version to pack: 0 with SHA, SHORT, VERSION and TRY set when it does,
# 1 when it does not, 2 when it could not be looked at (said on stderr; the next pass looks
# again).
decide() {
  local was tries then outcome tag
  # By its whole name: ls-remote also lists a ref that only ends in refs/heads/main, and anyone
  # who can push a branch can make one.
  SHA="$(limited 45 git -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=20 ls-remote "$BRIDGE_URL" refs/heads/main 2>/dev/null \
          | awk '$2 == "refs/heads/main" { print $1 }')"
  case "$SHA" in *[!0-9a-f]*) SHA="" ;; esac
  [ "${#SHA}" -ge 40 ] || { say "the bridge's main could not be read at $BRIDGE_URL."; return 2; }
  SHORT="${SHA:0:7}"; TRY=1; VERSION=""
  if [ -f "$SEEN" ] && read -r was tries then outcome < "$SEEN" && [ "$was" = "$SHA" ]; then
    case "$tries$then" in
      ""|*[!0-9]*) ;;   # not a record this script wrote: look again.
      *)
        [ "$outcome" = failed ] || return 1
        [ "$tries" -lt "$TRIES" ] || return 1
        [ $(( $(date +%s) - then )) -ge "$RETRY_AFTER" ] || return 1
        TRY=$((tries + 1)) ;;
    esac
  fi

  if [ ! -d "$WORK/source/.git" ]; then
    rm -rf "$WORK/source"
    limited 300 git clone -q --no-checkout "$BRIDGE_URL" "$WORK/source" >/dev/null 2>&1 \
      || { say "the bridge could not be cloned to $WORK/source."; return 2; }
  fi
  sgit remote set-url origin "$BRIDGE_URL" \
    && limited 120 git -C "$WORK/source" -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=20 \
         fetch -q --prune --prune-tags --tags --force origin >/dev/null 2>&1 \
    || { say "the bridge in $WORK/source could not be fetched."; return 2; }
  # Main moved while this looked: the next pass decides on where it is then.
  [ "$(sgit rev-parse -q --verify 'refs/remotes/origin/main^{commit}' 2>/dev/null)" = "$SHA" ] \
    || { say "the bridge's main moved while it was read."; return 2; }

  VERSION="$(sgit show "$SHA:src/Microsoft.SwiftUI/Microsoft.SwiftUI.csproj" 2>/dev/null \
    | sed -n 's#.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*#\1#p' | head -1)"
  if ! well_formed "$VERSION"; then
    seen nothing
    note nothing "The bridge's main ($SHORT) names no usable version in its project file, so nothing was packed."
    return 1
  fi
  tag="refs/tags/v$VERSION"
  sgit rev-parse -q --verify "$tag^{commit}" >/dev/null 2>&1 || return 0
  seen nothing
  if ! sgit merge-base --is-ancestor "$tag" "$SHA" 2>/dev/null; then
    note nothing "The bridge's main ($SHORT) names version $VERSION, and the tag v$VERSION is at a commit that is not on main, so nothing was packed. Give main the next version to have it packed."
  elif ! sgit diff --quiet "$tag" "$SHA" -- src native 2>/dev/null; then
    note nothing "The bridge's main ($SHORT) has changed the bridge's code since v$VERSION without a new version, so nothing was packed. A change reaches the app only under the next version."
  elif [ ! -f "$FEED/Microsoft.SwiftUI.$VERSION.nupkg" ]; then
    # A tag is not proof that the Captain gave the version out: the sessions' login can make one.
    note nothing "The bridge's main ($SHORT) is version $VERSION, which has its tag, but your feed ($FEED) has no package of it, so builds that pin it cannot restore. If the release v$VERSION is yours, download its package into the feed. If it is not, delete that tag and release and remove $SEEN, and the version is packed."
  else
    note nothing "Nothing to pack: the bridge's main ($SHORT) is version $VERSION, which has its tag."
  fi
  return 1
}

# Whether only the repository's admins can update the bridge's main: 0 when an active ruleset
# restricts updates and nobody but admins may bypass it, 1 when none does, 2 when it could not
# be read. (Rules add up, so one such ruleset is enough whatever the others allow.) A ruleset
# that does not say who may bypass it is not taken for one that nobody may: GitHub leaves the
# list out for a login that may not see it.
main_is_closed() {
  local ids id others
  ids="$(limited 60 gh api --paginate "repos/$BRIDGE_REPO/rules/branches/main" \
           --jq '.[] | select(.type == "update") | .ruleset_id' 2>> "$LOG")" || return 2
  for id in $ids; do
    case "$id" in *[!0-9]*) continue ;; esac
    others="$(limited 60 gh api "repos/$BRIDGE_REPO/rulesets/$id" --jq \
      'if (.bypass_actors | type) != "array" then "unknown" else [.bypass_actors[] | select(((.actor_type == "RepositoryRole" and .actor_id == 5) or .actor_type == "OrganizationAdmin") | not)] | length end' \
      2>> "$LOG")" || return 2
    [ "$others" != 0 ] || return 0
  done
  return 1
}

# Whether the package in the feed is one this script built from an earlier commit of main
# under the same version, which no release was made of.
fed_from_another_commit() {
  local commit version sum
  [ -f "$FED" ] && [ -f "$1" ] && read -r commit version sum < "$FED" || return 1
  [ "$commit" != "$SHA" ] && [ "$version" = "$VERSION" ] && [ "$(shasum -a 256 "$1" | cut -d' ' -f1)" = "$sum" ]
}

# The commit the tag v$VERSION is at on the bridge's repository, or nothing when it has none.
# 2 when that could not be read.
tag_at() {
  local listed
  listed="$(limited 45 git -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=20 ls-remote "$BRIDGE_URL" \
              "refs/tags/v$VERSION" "refs/tags/v$VERSION^{}" 2>/dev/null)" || return 2
  # An annotated tag is listed twice; the line that ends in ^{} is the commit.
  printf '%s\n' "$listed" | awk -v tag="refs/tags/v$VERSION" \
    '$2 == tag "^{}" { peeled = $1 } $2 == tag { plain = $1 } END { print (peeled != "" ? peeled : plain) }'
}

# Whether the package of $VERSION in the feed is the one this script put there from $SHA.
fed_by_this() {
  local commit version sum
  [ -f "$FED" ] && [ -f "$1" ] && read -r commit version sum < "$FED" || return 1
  [ "$commit" = "$SHA" ] && [ "$version" = "$VERSION" ] && [ "$(shasum -a 256 "$1" | cut -d' ' -f1)" = "$sum" ]
}

# Ends a release that did not get there, with why. $1 the reason, with no full stop.
failed() {
  local more="It is tried again in an hour (that was try $TRY of $TRIES)."
  [ "$TRY" -lt "$TRIES" ] \
    || more="That was try $TRY of $TRIES: it is not tried again until main changes, or until the file $SEEN is removed."
  seen failed
  note failed "Version $VERSION on the bridge's main ($SHORT) was not packed ($(now)): $1. $more The logs are in $WORK on Lilliput."
  echo "BRIDGE: version $VERSION on the bridge's main ($SHORT) was not packed: $1. $more"
  exit 0
}

# One step of the builder on the clean copy. $1 the step. Its output is left in STEP_OUT.
step() {
  local code
  STEP_OUT="$(SESSION_BRIDGE="$WORK/build" SESSION_BRIDGE_REPO="$BRIDGE_REPO" SESSION_BRIDGE_LOGS="$WORK" \
    /bin/bash "$BUILDER" "$1" 2> "$WORK/step.err")"
  code=$?
  { echo "== $1 (ended with $code)"; printf '%s\n' "$STEP_OUT"; cat "$WORK/step.err"; } >> "$LOG"
  # Why it stopped, in the helper's first sentence; where its log is, is said once, by failed.
  STEP_STOP="$(sed -n 's/^STOP: //p' "$WORK/step.err" | tail -1 | sed -e 's/\. .*//' -e 's/\.$//' | cut -c1-300)"
  rm -f "$WORK/step.err"
  return "$code"
}

release() {
  local build="$WORK/build" lock="$WORK/lock" holder tests="" kind count package target sum previous changes notes url at code lower mine extra="" pre="" proved=""
  mkdir -p "$WORK" "$OUT/state" || { say "$WORK could not be made."; return 2; }
  # One release at a time, also when someone runs this by hand during a pass. A lock whose
  # release is no longer running, or that is older than a release can take (its steps' limits
  # come to about 62 minutes), is one that died.
  if ! mkdir "$lock" 2>/dev/null; then
    holder="$(cat "$lock/pid" 2>/dev/null)"
    case "$holder" in *[!0-9]*) holder="" ;; esac
    if { [ -n "$holder" ] && ! kill -0 "$holder" 2>/dev/null; } || [ -n "$(find "$lock" -maxdepth 0 -mmin +75 2>/dev/null)" ]; then
      rm -rf "$lock"
      mkdir "$lock" 2>/dev/null || { say "the lock $lock could not be taken."; return 2; }
    else
      say "another release is running ($lock)."; return 2
    fi
  fi
  echo $$ > "$lock/pid"
  trap 'rm -rf "$WORK/lock"' EXIT
  decide || { [ $? = 1 ] && return 0; return 2; }
  # From here on every end rewrites the note.
  note packing "Packing version $VERSION from the bridge's main ($SHORT), started $(now). It takes about five minutes; sessions wait for it."
  { echo "Release of $VERSION from $SHA, try $TRY, $(now)."; } > "$LOG"

  # What is on main is the Captain's only while nobody else can put anything there.
  main_is_closed; code=$?
  [ "$code" != 2 ] || failed "the rules of the bridge's main could not be read"
  [ "$code" = 0 ] \
    || failed "no ruleset restricts updates of the bridge's main to the repository's admins, so what is on it is not known to be what you merged"

  target="$FEED/Microsoft.SwiftUI.$VERSION.nupkg"
  [ -d "$FEED" ] || failed "your feed folder $FEED is not there"
  if fed_by_this "$target"; then
    # An earlier try got as far as the feed: the release is made from that package.
    tests="$(sed -n 2p "$FED")"
    echo "The package of an earlier try is in the feed; not built again." >> "$LOG"
  elif [ -e "$target" ] && ! fed_from_another_commit "$target"; then
    failed "your feed already holds a build of $VERSION that this check did not put there ($target). Nothing was replaced and nothing was tagged: move that file away, or give the bridge's main the next version"
  else
    # Its own package from where main was before, of which no release was made (the version
    # has no tag), gives way to one from where main is now.
    if [ -e "$target" ]; then
      echo "The feed holds this check's own package of $VERSION from $(cut -c1-7 "$FED"), never released; replaced by one from $SHORT." >> "$LOG"
      rm -f "$target" || failed "the package of an earlier commit could not be taken out of $FEED"
    fi
    rm -rf "$build"
    git clone -q --no-checkout "$WORK/source" "$build" >> "$LOG" 2>&1 \
      && git -C "$build" checkout -q --detach "$SHA" >> "$LOG" 2>&1 \
      && [ "$(git -C "$build" rev-parse HEAD 2>/dev/null)" = "$SHA" ] \
      || failed "a clean copy of $SHORT could not be made in $build"

    step test || failed "the bridge's tests did not pass${STEP_STOP:+ ($STEP_STOP)}"
    # Both counts the step said, the managed tests' and then the Swift side's, on one line:
    # it is the second line of the fed file and goes into the notes and the status as it is.
    for kind in Managed Swift; do
      count="$(printf '%s\n' "$STEP_OUT" | grep -Eo "$kind tests: [0-9]+/[0-9]+ passed" | tail -1)"
      [ -z "$count" ] || tests="${tests:+$tests, }$count"
    done
    step package || failed "the pack did not pass${STEP_STOP:+ ($STEP_STOP)}"
    package="$(printf '%s\n' "$STEP_OUT" | sed -n 's/^PACKAGE=//p' | tail -1)"
    [ "$(printf '%s\n' "$STEP_OUT" | sed -n 's/^VERSION=//p' | tail -1)" = "$VERSION" ] && [ -f "$package" ] \
      || failed "the pack left no package of version $VERSION"

    # Recorded before it is there, then put under a name no restore reads and moved into
    # place, so no build sees half a package and a try that ends here knows its own file.
    sum="$(shasum -a 256 "$package" | cut -d' ' -f1)"
    printf '%s %s %s\n%s\n' "$SHA" "$VERSION" "$sum" "$tests" > "$FED.new" && mv "$FED.new" "$FED" \
      && [ ! -e "$target" ] \
      && cp "$package" "$FEED/.Microsoft.SwiftUI.$VERSION.part" && mv "$FEED/.Microsoft.SwiftUI.$VERSION.part" "$target" \
      && fed_by_this "$target" \
      || { rm -f "$FEED/.Microsoft.SwiftUI.$VERSION.part"; failed "the package could not be put in $FEED"; }
  fi

  # The sessions' login can make a tag. One of this version that is not at main's commit was
  # not made here, and a release would be hung on it whatever --target says.
  at="$(tag_at)" || failed "the package is in your feed, but the tags of $BRIDGE_REPO could not be read, so the release v$VERSION was not made"
  [ -z "$at" ] || [ "$at" = "$SHA" ] \
    || failed "the package is in your feed, but a tag v$VERSION has appeared on $BRIDGE_REPO at ${at:0:7}, which is not main's commit, so the release was not made. If that tag is not yours, delete it and remove $SEEN"

  sum="$(shasum -a 256 "$target" | cut -d' ' -f1)"
  previous="$(sgit describe --tags --abbrev=0 --match 'v*' "$SHA" 2>/dev/null)"
  changes="$(sgit log --no-merges --format='- %s' ${previous:+"$previous.."}"$SHA" 2>/dev/null | head -20)"
  # (bash 3.2 cannot hold an apostrophe inside \${...:+...}, so the optional parts are built first.)
  [ -z "$tests" ] || proved=" ($tests)"
  [ -z "$changes" ] || changes="$changes

"
  notes="Packed from main at $SHORT by the feedback check on the Captain's Mac, with scripts/build-xcframework.sh and scripts/pack-and-verify.sh, after the bridge's own tests passed$proved. Download into ~/work/Microsoft.SwiftUI/artifacts (the app's local NuGet feed).

${changes}NuGet package SHA-256: $sum"
  case "$VERSION" in *-*) pre=--prerelease ;; esac
  url="$(limited 600 gh release create "v$VERSION" "$target" --repo "$BRIDGE_REPO" --target "$SHA" \
           --title "$VERSION (local feed package)" --notes "$notes" $pre 2>> "$LOG" | tail -1)" \
    || failed "the package is in your feed, but the release v$VERSION could not be made on $BRIDGE_REPO"
  case "$url" in https://*) ;; *) url="https://github.com/$BRIDGE_REPO/releases/tag/v$VERSION" ;; esac
  at="$(tag_at)"; code=$?
  if [ "$code" != 0 ]; then
    extra=" Its tag could not be read back to see that it is at $SHORT."
  elif [ "$at" != "$SHA" ]; then
    failed "the release v$VERSION was made ($url), but its tag is at ${at:-no commit that could be read}, not at main's $SHORT: someone else made that tag first. Delete the release and the tag, then remove $SEEN, and it is made again"
  fi

  # A copy of this version in the Captain's package folder is what his builds would use.
  lower="$(printf '%s' "$VERSION" | tr '[:upper:]' '[:lower:]')"
  mine="$PACKAGES/microsoft.swiftui/$lower"
  if [ -d "$mine" ] && ! cmp -s "$mine/microsoft.swiftui.$lower.nupkg" "$target"; then
    extra="$extra Your package folder holds another build of $VERSION ($mine): remove that folder so that your builds take this one."
  fi
  limited 120 git -C "$WORK/source" fetch -q --tags --force origin >/dev/null 2>&1
  rm -rf "$build"
  seen released
  note released "Version $VERSION was packed from the bridge's main ($SHORT), put in your feed and tagged v$VERSION on $(now), after the bridge's own tests passed$proved: $url.$extra"
  echo "BRIDGE: packed $VERSION from the bridge's main ($SHORT), in the feed and tagged ($url).$extra"
}

case "${1:-}" in
  due)
    mkdir -p "$WORK" "$OUT/state" || exit 2
    decide; code=$?
    [ "$code" = 0 ] || { [ "$code" = 1 ] && exit 0; exit 2; }
    echo "$VERSION $SHORT" ;;
  release) release ;;
  *) sed -n '2,/^set -uo pipefail/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//' >&2; exit 2 ;;
esac
