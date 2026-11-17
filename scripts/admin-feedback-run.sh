#!/bin/bash
# What launchd starts for the feedback check. It brings the check's own checkout to origin/main
# and then runs scripts/admin-feedback-check.sh from it, so the check always runs what is on
# main and never depends on which branch the Captain's working checkout happens to be on.
#
#   scripts/admin-feedback-run.sh [the arguments of admin-feedback-check.sh]
#
# The check's own checkout is a git worktree with a detached HEAD
# (~/work/copilot-worktrees/SentenceStudio/feedback-runner on Lilliput, made with
# `git worktree add --detach <path> origin/main`). Only such a checkout is ever moved: started
# from a checkout that is on a branch, that has changes, or that is at a commit main does not
# hold, this script moves nothing and runs what is there.
#
# The whole script is one function, read in full before it runs, because the update can replace
# this file.
main() {
  local root note=""
  root="$(cd "$(dirname "$0")/.." && pwd)" || exit 1
  export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH"

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

  FEEDBACK_CHECK_UPDATE="$note" exec /bin/bash "$root/scripts/admin-feedback-check.sh" "$@"
}
main "$@"
exit 1
