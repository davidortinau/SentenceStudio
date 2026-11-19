#!/bin/bash
# What launchd starts for the feedback check. It brings the check's own checkout to origin/main
# and then runs scripts/admin-feedback-check.sh from it, so the check always runs what is on
# main and never depends on which branch the Captain's working checkout happens to be on.
#
#   scripts/admin-feedback-run.sh [the arguments of admin-feedback-check.sh]
#   scripts/admin-feedback-run.sh --loop
#
# --loop is what launchd starts, every five minutes. It runs admin-feedback-check.sh --pass,
# bringing the checkout to origin/main before each pass, until a pass finds nothing to do (exit
# 10), two passes in a row fail, or MAX_PASSES have run. After two failures it waits an hour
# (state/loop-paused-until) before passing again, so a broken setup is not retried every five
# minutes. launchd never starts the job again while it is still running.
#
# The check's own checkout is a git worktree with a detached HEAD
# (~/work/copilot-worktrees/SentenceStudio/feedback-runner on Lilliput, made with
# `git worktree add --detach <path> origin/main`). Only such a checkout is ever moved: started
# from a checkout that is on a branch, that has changes, or that is at a commit main does not
# hold, this script moves nothing and runs what is there.
#
# The whole script is read in full before main runs, because the update can replace this file.
main() {
  local root
  root="$(cd "$(dirname "$0")/.." && pwd)" || exit 1
  export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH"

  if [ "${1:-}" != --loop ]; then
    update
    FEEDBACK_CHECK_UPDATE="$note" exec /bin/bash "$root/scripts/admin-feedback-check.sh" "$@"
  fi

  local state="${FEEDBACK_CHECK_DIR:-$HOME/sstudio-feedback-check}/state" passes=0 failures=0 code until
  local MAX_PASSES=40
  mkdir -p "$state"
  until="$(cat "$state/loop-paused-until" 2>/dev/null)"
  case "$until" in ""|*[!0-9]*) ;; *) [ "$(date +%s)" -lt "$until" ] && exit 0 ;; esac
  rm -f "$state/loop-paused-until"
  while [ "$passes" -lt "$MAX_PASSES" ]; do
    update
    FEEDBACK_CHECK_UPDATE="$note" /bin/bash "$root/scripts/admin-feedback-check.sh" --pass
    code=$?
    passes=$((passes + 1))
    case "$code" in
      10) break ;;
      0) failures=0 ;;
      *)
        failures=$((failures + 1))
        if [ "$failures" -ge 2 ]; then
          echo $(( $(date +%s) + 3600 )) > "$state/loop-paused-until"
          # The status issue says so: no pass rewrites it during the pause.
          python3 -I "$root/scripts/admin-feedback-status.py" update \
            --windows "$state/fix-session-windows" --handled "$state/handled-requests" \
            >/dev/null 2> "$state/status.err" && rm -f "$state/status.err"
          break
        fi ;;
    esac
  done
  exit 0
}

# Brings the checkout to origin/main when it is safe to, and says in $note when it was not.
update() {
  note=""
  if [ "$(git -C "$root" rev-parse --abbrev-ref HEAD 2>/dev/null)" != HEAD ]; then
    note="Not updated: the checkout is on the branch $(git -C "$root" rev-parse --abbrev-ref HEAD 2>/dev/null), so it was run as it is."
  elif [ -n "$(git -C "$root" status --porcelain 2>/dev/null)" ]; then
    note="Not updated: the checkout has uncommitted changes, so it was run as it is."
  elif ! git -C "$root" fetch -q origin main 2>/dev/null; then
    note="Not updated: origin/main could not be fetched, so the last copy was run."
  elif ! git -C "$root" merge-base --is-ancestor HEAD origin/main 2>/dev/null; then
    note="Not updated: the checkout is at a commit that is not on main, so it was run as it is."
  elif ! git -C "$root" checkout -q --detach origin/main 2>/dev/null; then
    note="Not updated: origin/main could not be checked out, so the last copy was run."
  fi
}
main "$@"
exit 1
