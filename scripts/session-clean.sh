#!/bin/bash
# A clean start for a fix session: what an earlier session left in the session user's home
# does not reach the next one (Captain, 2026-11-27: "address the clean home issue so we don't
# pollute future sessions").
#
# Run as the session's user by scripts/admin-feedback-check.sh, which reads this file itself
# and hands it over on standard input (that user cannot read the checkout), with HOME set and
# nothing of that user's running:
#
#   session-clean.sh image
#       Writes a tar of the home's tool folders to standard output: everything at its top
#       level except what is kept in place (KEEP), what is never taken (never_taken), and the
#       large caches inside it (BULK). The runner keeps it, on the Captain's side, as the
#       image every session starts from.
#   session-clean.sh clean <stamp> <image folder> <temporary folder> [--dry-run]
#       Puts the home back to the image the runner has just unpacked into <image folder>:
#       1. everything at the top level of the home that is neither kept nor in the image is
#          moved to ~/sstudio/set-aside/<stamp>/ (Claude Code's own state and memory, a shell
#          profile, a git configuration: whatever a session or a tool left);
#       2. the folders the image holds are made equal to it, file for file by content;
#       3. the folders a session's files are loaded from (~/sstudio and what is in it) lose
#          anything Claude Code would read as instructions, ~/sstudio/fix anything that is
#          not one of the check's own files, and ~/sstudio/gh anything but the sign-in the
#          runner writes there, to the same set-aside folder;
#       4. <temporary folder> is emptied: the session user's own, named by the runner. This
#          script never works out a temporary folder by itself (it did once, on 2026-11-27,
#          and a test run as the Captain emptied his); "-" leaves that step out. Only one of
#          two kinds of folder is ever emptied: the running user's own per-user temporary
#          folder (/var/folders/../../T) when HOME is that user's real home, or a stand-in
#          that holds a file named .sstudio-session-tmp (what a test makes);
#       5. set-aside folders older than 14 days are deleted.
#       Prints "aside <name>" for each thing moved, "respelled <name>" for a kept thing
#       given its own spelling back, "changed <path>" for each file put back (the first 200),
#       "restored <count>", and "left-in-temporary-folder <count>" when something there could
#       not be removed. With --dry-run it changes nothing.
#
# Whose home: only one the runner marked as a session user's (~/sstudio/.session-home, written
# by clean_start in scripts/admin-feedback-check.sh, which never runs it for its own user),
# and never the real home of an administrator or of root. Run by hand against any other home
# it refuses, before it moves anything.
#
# Nothing is deleted outright except temporary files, what step 2 replaces inside the tool
# folders (their names are printed), and set-aside folders after 14 days. Exit 1, with the
# reason on standard error, when the home could not be put back.
set -uo pipefail
export PATH=/usr/bin:/bin:/usr/sbin:/sbin

# Kept in place, never looked into: the Claude token the Captain made, what macOS keeps for
# the account (keychain, preferences, the simulators), and the check's own folder.
KEEP=".claude-token .CFUserTextEncoding .Trash Library sstudio Desktop Documents Downloads Movies Music Pictures Public"
# Large caches inside the tool folders, kept as they are and not part of the image: restored
# packages, the package download cache, and the Docker machine's disk.
BULK=".nuget/packages .local/share/NuGet .colima/_lima"
# The file the runner writes into the image when it is complete: its top-level names.
MARK=".sstudio-home-image"

fail() { echo "$1" >&2; exit 1; }
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
kept() { case "$1" in *[[:space:]]*) return 1 ;; esac; case " $KEEP " in *" $1 "*) return 0 ;; esac; return 1; }
# The kept name that $1 is another spelling of: the disk does not tell "library" from
# "Library", so a kept thing that was renamed by case alone is still the kept thing, and is
# neither taken into an image nor set aside.
respelled() {
  local k
  for k in $KEEP; do
    [ "$1" != "$k" ] && [ "$(lower "$1")" = "$(lower "$k")" ] && [ "$HOME/$1" -ef "$HOME/$k" ] \
      && { printf '%s' "$k"; return 0; }
  done
  return 1
}

# Never part of an image, whatever is there when one is taken: Claude Code's state and
# memory, sign-ins, and the files a shell, git or Claude Code reads as configuration or
# instructions. They are set aside before every session like anything else not in the image.
never_taken() {
  case "$(lower "$1")" in
    .claude|.claude.json*|.azure|.azd|.cache|.netrc|.env|.mcp.json|claude*.md|agents.md) return 0 ;;
    .z*|.bash*|.profile|.inputrc|.git*|.curlrc|.wgetrc|.vim*|.lesshst|.python*|.node*) return 0 ;;
  esac
  return 1
}

# Every entry at the top level of a folder, hidden ones too, each ended by a NUL: a name may
# hold a line break. $1 the folder.
entries() {
  local path
  for path in "$1"/.[!.]* "$1"/..?* "$1"/*; do
    { [ -e "$path" ] || [ -L "$path" ]; } && printf '%s\0' "$path"
  done
}
# A name as it may be printed: one line, nothing that is not a plain character.
plain() { printf '%s' "$1" | LC_ALL=C tr -c 'A-Za-z0-9._ -' '?'; }

case "${HOME:-}" in /*) ;; *) fail "HOME is not set to a folder" ;; esac
[ -d "$HOME" ] && [ ! -L "$HOME" ] || fail "$HOME is not a folder"

mode="${1:-}"

if [ "$mode" = image ]; then
  names=() excludes=()
  while IFS= read -r -d '' path; do
    name="${path##*/}"
    kept "$name" && continue
    respelled "$name" >/dev/null && continue
    never_taken "$name" && continue
    names+=("./$name")
  done < <(entries "$HOME")
  [ "${#names[@]}" -gt 0 ] || fail "there is nothing in $HOME to take an image of"
  for part in $BULK; do excludes+=(--exclude "./$part"); done
  # A git configuration inside a folder that is taken would be read by every git command.
  excludes+=(--exclude "./.config/git")
  exec /usr/bin/tar -cf - -C "$HOME" "${excludes[@]}" "${names[@]}"
fi

[ "$mode" = clean ] || fail "usage: session-clean.sh image | clean <stamp> <image folder> <temporary folder or -> [--dry-run]"
stamp="${2:-}"; image="${3:-}"; tmp="${4:-}"; dry=no; [ "${5:-}" = --dry-run ] && dry=yes
[ -n "$tmp" ] || fail "the temporary folder to empty was not named (- for none)"
home="$(cd "$HOME" && pwd -P)" || fail "$HOME could not be read"
# The home the system has on record for whoever runs this, which no HOME setting changes.
own_home="$(perl -e 'print((getpwuid($<))[7] // "")' 2>/dev/null)"
own_home="$(cd "${own_home:-/nonexistent}" 2>/dev/null && pwd -P)" || own_home=""
[ -d "$HOME/sstudio" ] && [ ! -L "$HOME/sstudio" ] || fail "$HOME/sstudio is not a plain folder: nothing was moved"
case "$stamp" in ""|*[!A-Za-z0-9]*) fail "the stamp has to be letters and digits" ;; esac
[ -f "$HOME/sstudio/.session-home" ] && [ ! -L "$HOME/sstudio/.session-home" ] \
  || fail "$HOME is not marked as a session user's home (no sstudio/.session-home): nothing was moved"
if [ "$home" = "$own_home" ]; then
  [ "$(id -u)" != 0 ] || fail "this is root's own home: nothing was moved"
  case " $(id -Gn) " in *" admin "*|*" wheel "*|*" sudo "*) fail "this is the home of an administrator ($(id -un)): nothing was moved" ;; esac
fi
# The temporary folder: never one worked out here, and only one of the two kinds that can be
# told to be a session's.
if [ "$tmp" != - ]; then
  case "$tmp" in /*) ;; *) fail "the temporary folder $tmp is not a full path" ;; esac
  [ -d "$tmp" ] && [ ! -L "$tmp" ] && [ -O "$tmp" ] || fail "the temporary folder $tmp is not a folder of this user's own"
  tmp="$(cd "$tmp" && pwd -P)" || fail "the temporary folder could not be read"
  [ "$tmp" != / ] || fail "the temporary folder / is not one to empty"
  case "$home/" in "$tmp"/*) fail "the temporary folder $tmp holds the home" ;; esac
  case "$tmp/" in "$home"/*) fail "the temporary folder $tmp is inside the home, not one to empty" ;; esac
  if [ -f "$tmp/.sstudio-session-tmp" ] && [ ! -L "$tmp/.sstudio-session-tmp" ]; then
    :   # a stand-in, marked by whoever made it
  else
    case "$tmp" in
      /var/folders/*/*/T|/private/var/folders/*/*/T) ;;
      *) fail "the temporary folder $tmp is neither a per-user temporary folder nor marked as a stand-in, not one to empty" ;;
    esac
    [ "$home" = "$own_home" ] || fail "the temporary folder $tmp is a real one and $HOME is not this user's real home: not emptied"
  fi
fi
[ -d "$image" ] && [ ! -L "$image" ] || fail "the image folder $image is not there"
[ -f "$image/$MARK" ] || fail "the image in $image is not complete (no $MARK)"
while IFS= read -r name; do
  [ -z "$name" ] && continue
  { [ -e "$image/$name" ] || [ -L "$image/$name" ]; } || fail "the image in $image lacks $name"
  kept "$name" && fail "the image in $image holds $name, which is kept in place"
done < "$image/$MARK"
# The token is the one thing in the home a session needs that the image does not hold.
[ -f "$HOME/.claude-token" ] && [ ! -L "$HOME/.claude-token" ] || fail "$HOME/.claude-token is not a plain file"

# What is set aside must land in the home's own folder for it, not wherever a link points. A
# link or a file a session put where that folder goes is moved out of the way under a name
# that says so (a link is moved as the link it is), so that it cannot stop every later start.
asides="$HOME/sstudio/set-aside"
if [ -L "$asides" ] || { [ -e "$asides" ] && [ ! -d "$asides" ]; }; then
  echo "aside sstudio-set-aside"
  if [ "$dry" = no ]; then
    mv "$asides" "$HOME/sstudio/set-aside-was-$stamp" || fail "$asides is not a folder and could not be moved out of the way"
  fi
fi
aside="$asides/$stamp"
[ ! -L "$aside" ] || fail "$aside is a link: nothing was moved"
# Moves one thing to the set-aside folder under a name that says where it was. $1 the path,
# $2 the name to give it.
set_aside() {
  local to="$aside/$2" n=1
  case "$1" in "$HOME"/*) ;; *) fail "a thing to set aside is not in the home: $(plain "$1")" ;; esac
  echo "aside $(plain "$2")"
  [ "$dry" = no ] || return 0
  mkdir -p "$aside" || fail "the set-aside folder $aside could not be made"
  while [ -e "$to" ] || [ -L "$to" ]; do n=$((n + 1)); to="$aside/$2.$n"; done
  mv "$1" "$to" 2>/dev/null && return 0
  # A file its owner locked, or a folder it closed to itself, is still its owner's to move.
  chflags -R nouchg,nouappnd "$1" 2>/dev/null; chmod -RN "$1" 2>/dev/null; chmod -R u+rwX "$1" 2>/dev/null
  chflags nouchg,nouappnd "$HOME" 2>/dev/null
  mv "$1" "$to" || fail "$(plain "$1") could not be set aside"
}

# 1. The top level of the home: what is neither kept nor in the image.
unmoved=""
while IFS= read -r -d '' path; do
  name="${path##*/}"
  kept "$name" && continue
  # A kept thing under another spelling gets its own back, where the rules below know it.
  if own="$(respelled "$name")"; then
    echo "respelled $(plain "$name")"
    if [ "$dry" = no ]; then
      mv "$HOME/$name" "$HOME/$own" || fail "$(plain "$name") could not be given its name $own back"
    else
      unmoved="$unmoved/$name
"
    fi
    continue
  fi
  # The image's own mark is not something a home holds, and the rules below would keep it.
  if [ "$(lower "$name")" != "$MARK" ]; then
    { [ -e "$image/$name" ] || [ -L "$image/$name" ]; } && continue
  fi
  set_aside "$path" "$name"
  # A dry run leaves it where it is, and step 2 must not count it as something to delete.
  [ "$dry" = no ] || unmoved="$unmoved/$name
"
done < <(entries "$HOME")

# 2. The image's folders, made equal to it. What is kept in place and the large caches are
# left out, which also keeps them from being deleted.
rules="$image.rules"
{ printf '/%s\n' "$MARK"
  for name in $KEEP $BULK; do printf '/%s\n' "$name"; done
  printf '%s' "$unmoved"; } > "$rules" || fail "the list of what is kept could not be written"
options="-a -c --delete -i"; [ "$dry" = no ] || options="$options -n"
# rsync gives the home the permissions of the folder it copies from, and the home's own are
# the account's (a session user's home is closed to others): the image's folder gets them first.
chmod "$(stat -f %Lp "$HOME")" "$image" || fail "the image folder could not be given the home's permissions"
# shellcheck disable=SC2086
changes="$(rsync $options --exclude-from="$rules" "$image/" "$HOME/" 2>"$rules.err")"
code=$?
if [ "$code" -ne 0 ] && [ "$dry" = no ]; then
  # A file its owner locked, or a folder it closed to itself, stops rsync and is still its
  # owner's to replace: the image's folders are opened (rsync then gives them the image's
  # permissions) and it is asked once more.
  while IFS= read -r name; do
    [ -n "$name" ] && [ -e "$HOME/$name" ] && [ ! -L "$HOME/$name" ] || continue
    chflags -R nouchg,nouappnd "$HOME/$name" 2>/dev/null; chmod -R u+rwX "$HOME/$name" 2>/dev/null
  done < "$image/$MARK"
  # shellcheck disable=SC2086
  changes="$(rsync $options --exclude-from="$rules" "$image/" "$HOME/" 2>"$rules.err")"
  code=$?
fi
rm -f "$rules"
if [ "$code" -ne 0 ]; then
  why="$(head -c 400 "$rules.err" | tr '\n' ' ')"; rm -f "$rules.err"
  fail "the home could not be made equal to its image (rsync ended with $code: $why)"
fi
rm -f "$rules.err"
# Only what was made, deleted, or differed in content, size or permissions is a change worth
# naming: a time alone that differed (a folder's, a link's, a file a tool rewrote as it was)
# is set right and not counted.
changes="$(printf '%s\n' "$changes" | grep -E '^(\*deleting|..[c+]|...s|.....p)' | grep -v '^\.d' || true)"
[ -z "$changes" ] || printf '%s\n' "$changes" | head -200 | sed 's/^[^ ]* */changed /'
count=0; [ -z "$changes" ] || count=$(( $(printf '%s\n' "$changes" | wc -l) ))
echo "restored $count"

# 3. Where a session's files are loaded from. Claude Code reads CLAUDE.md, AGENTS.md and a
# .claude folder from the folders above its worktree and from a folder it is given, so none
# may sit in ~/sstudio, beside the worktrees and clones, or among the check's own files; and
# among those files only the check's own (issue-<n>-...) stay.
for folder in "$HOME/sstudio" "$HOME/sstudio/trees" "$HOME/sstudio/clones" "$HOME/sstudio/fix"; do
  [ -d "$folder" ] && [ ! -L "$folder" ] || continue
  while IFS= read -r -d '' path; do
    name="${path##*/}"
    case "$(lower "$name")" in
      claude.md|claude.local.md|agents.md|.claude|.mcp.json|.git|.gitconfig|.gitattributes)
        set_aside "$path" "sstudio-${folder##*/}-$name" ;;
      issue-[0-9]*) ;;
      *) [ "$folder" != "$HOME/sstudio/fix" ] || set_aside "$path" "sstudio-fix-$name" ;;
    esac
  done < <(entries "$folder")
done

# The folder the session's gh reads: the runner writes its sign-in there for each session and
# removes it after. A sign-in left behind is deleted, and anything else (a gh configuration
# with aliases, say) set aside.
if [ -d "$HOME/sstudio/gh" ] && [ ! -L "$HOME/sstudio/gh" ]; then
  while IFS= read -r -d '' path; do
    name="${path##*/}"
    if [ "$name" = hosts.yml ] && [ ! -L "$path" ]; then
      [ "$dry" = yes ] || rm -f "$path"
    else
      set_aside "$path" "sstudio-gh-$name"
    fi
  done < <(entries "$HOME/sstudio/gh")
fi

# 4. The temporary folder the runner named. Nothing of this user's is running, so nothing in
# it is in use. What its owner locked or closed is opened first; what is still there after
# that is the system's own (macOS keeps protected folders there) and is counted, not fought.
if [ "$tmp" != - ] && [ "$dry" = no ]; then
  # (One find for each: with several "-exec ... +" in one, the system's find runs the last
  # first, and the removal would come before what makes it possible.)
  find "$tmp" -mindepth 1 -maxdepth 1 ! -name .sstudio-session-tmp -exec chflags -R nouchg,nouappnd {} + 2>/dev/null
  find "$tmp" -mindepth 1 -maxdepth 1 ! -name .sstudio-session-tmp -exec chmod -R u+rwX {} + 2>/dev/null
  find "$tmp" -mindepth 1 -maxdepth 1 ! -name .sstudio-session-tmp -exec rm -rf {} + 2>/dev/null
  left=$(( $(find "$tmp" -mindepth 1 -maxdepth 1 ! -name .sstudio-session-tmp 2>/dev/null | wc -l) ))
  [ "$left" -eq 0 ] || echo "left-in-temporary-folder $left"
fi

# 5. Old set-aside folders.
if [ "$dry" = no ] && [ -d "$HOME/sstudio/set-aside" ]; then
  find "$HOME/sstudio/set-aside" -mindepth 1 -maxdepth 1 -type d -mtime +14 -exec chmod -R u+rwX {} + 2>/dev/null
  find "$HOME/sstudio/set-aside" -mindepth 1 -maxdepth 1 -type d -mtime +14 -exec rm -rf {} + 2>/dev/null
fi
exit 0
