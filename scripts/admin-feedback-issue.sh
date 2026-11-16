#!/bin/bash
# GitHub issues for the feedback check (docs/admin-feedback-check.md): look for an issue that
# already tracks a problem, read one, or file one. Only this repository, whatever is asked.
#
#   scripts/admin-feedback-issue.sh search <words...>   open and closed issues matching the words
#   scripts/admin-feedback-issue.sh view <number>       one issue: title, state, body
#   scripts/admin-feedback-issue.sh create <title> <body>   file an issue
#
# The repository is public. An issue must carry none of a learner's text (the rules are in the
# doc above). This script cannot tell whose words a sentence is, so that rule is the writer's
# to keep; what it can refuse, it does: an email address, a profile id, an agent token, or a
# body too long to be a plain description of a problem.
set -euo pipefail

REPO=davidortinau/SentenceStudio
MAX_TITLE=120
MAX_BODY=3000

usage() { sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

refuse_private() {
  # $1 what it is, $2 the text
  if printf '%s' "$2" | grep -Eq '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'; then
    echo "STOP: the $1 holds an email address. An issue carries no private text." >&2; exit 2
  fi
  if printf '%s' "$2" | grep -Eiq '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'; then
    echo "STOP: the $1 holds what looks like a profile id. Refer to the report by its own id only." >&2; exit 2
  fi
  if printf '%s' "$2" | grep -q 'ssadm_'; then
    echo "STOP: the $1 holds an agent token." >&2; exit 2
  fi
}

case "${1:-}" in
  search)
    [ $# -ge 2 ] || usage
    shift
    gh issue list --repo "$REPO" --state all --limit 20 --search "$*" \
      --json number,title,state,labels --jq '.[] | "#\(.number) [\(.state)] \(.title)"'
    ;;
  view)
    [ $# -eq 2 ] || usage
    case "$2" in ""|*[!0-9]*) echo "STOP: give an issue number" >&2; exit 2 ;; esac
    gh issue view "$2" --repo "$REPO" --json number,title,state,body \
      --jq '"#\(.number) [\(.state)] \(.title)\n\n\(.body)"'
    ;;
  create)
    [ $# -eq 3 ] || usage
    TITLE="$2"
    BODY="$3"
    [ -n "$TITLE" ] && [ "${#TITLE}" -le "$MAX_TITLE" ] \
      || { echo "STOP: the title must be 1 to $MAX_TITLE characters" >&2; exit 2; }
    [ -n "$BODY" ] && [ "${#BODY}" -le "$MAX_BODY" ] \
      || { echo "STOP: the body must be 1 to $MAX_BODY characters" >&2; exit 2; }
    refuse_private title "$TITLE"
    refuse_private body "$BODY"
    printf '%s' "$BODY" | gh issue create --repo "$REPO" --title "$TITLE" --body-file -
    ;;
  *) usage ;;
esac
