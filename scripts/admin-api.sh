#!/bin/bash
# Calls the admin API as the maintenance agent, with the agent token (docs/admin-api.md).
#
#   scripts/admin-api.sh whoami                          the token: id, permissions, when it expires
#   scripts/admin-api.sh sam [filter]                    flagged Sam answers. filter is attention (the
#                                                        default: new, or flagged again since they were
#                                                        triaged), all, or one status: new, reviewed,
#                                                        accepted, deferred, dismissed
#   scripts/admin-api.sh sam-show <reportId>             one report and each flag on it, in codes
#   scripts/admin-api.sh sam-exchange <reportId>         the flagged question and answer (LEARNER CONTENT)
#   scripts/admin-api.sh sam-triage <reportId> <flagCount> <status|keep> [issueNumber|none]
#                                                        say the report was looked at as it stood with
#                                                        <flagCount> flags (its flagCount as you read
#                                                        it; refused if the learner has flagged again
#                                                        since). Set the status, or keep it; with a
#                                                        number, link that GitHub issue; with none,
#                                                        unlink it; with neither, the link stays
#   scripts/admin-api.sh sam-counts [sinceDate]          what Sam's ledger counts about its own turns
#                                                        (retried empty announcements, off-topic,
#                                                        limits, timeouts), added up across learners
#                                                        by kind and code. sinceDate is a UTC day,
#                                                        yyyy-mm-dd; without it, everything still kept
#   scripts/admin-api.sh github                          reports filed to GitHub from the app
#   scripts/admin-api.sh screen-state <reference>        a GitHub report's saved screen state (LEARNER CONTENT)
#
# Prints the API's JSON on stdout and exits 0 for a 200. Anything else prints the status and the
# API's failure code on stderr and exits 1 (2 for a usage or token problem), so a caller can
# never mistake a refusal for an empty inbox.
#
# Production: the token is read from Key Vault (dbkv-rsn72awybem6s, secret admin-agent-token)
# through `az`, which must be signed in. Reading that one secret is covered by the Captain's
# standing go (docs/admin-portal-plan.md); nothing else in Azure is.
# Any other API: set ADMIN_API_BASE and ADMIN_AGENT_TOKEN together. The production token is
# never read for, or sent to, anything but the production API, and ADMIN_AGENT_TOKEN is never
# sent to production. The token is never printed and never placed on a command line.
set -euo pipefail

VAULT=dbkv-rsn72awybem6s
SECRET=admin-agent-token
PROD_HOST=api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io
PROD="https://$PROD_HOST"

# Where the request goes. Anything that names the production host, however it is spelled (upper
# case, a port, extra slashes), is production and is reached only as $PROD: the choice of token
# below turns on this, and must not turn on spelling.
BASE="${ADMIN_API_BASE:-$PROD}"
BASE_HOST="$(python3 -I -c 'import sys
from urllib.parse import urlsplit, unquote
try:
    # As curl will read it: percent-escapes decoded, no trailing dot, lower case.
    print(unquote(urlsplit(sys.argv[1]).hostname or "").rstrip(".").lower())
except ValueError:
    print("")' "$BASE")"
if [ -z "$BASE_HOST" ]; then
  echo "STOP: ADMIN_API_BASE is not a URL with a host (for example http://localhost:5081)." >&2
  exit 2
fi
if [ "$BASE_HOST" = "$PROD_HOST" ]; then
  BASE="$PROD"
else
  while [ "${BASE%/}" != "$BASE" ]; do BASE="${BASE%/}"; done
fi

usage() { sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

# An id travels in the URL path, so it is held to the characters a real id uses.
check_id() {
  case "$1" in
    ""|*[!A-Za-z0-9._-]*) echo "STOP: '$1' is not an id" >&2; exit 2 ;;
  esac
}

METHOD=GET; ROUTE=""; BODY=""
case "${1:-}" in
  whoami) [ $# -eq 1 ] || usage; ROUTE=/api/admin/token ;;
  sam)
    [ $# -le 2 ] || usage
    case "${2:-attention}" in
      attention) ROUTE="/api/admin/reports/sam?take=200&attention=true" ;;
      all) ROUTE="/api/admin/reports/sam?take=200" ;;
      new|reviewed|accepted|deferred|dismissed) ROUTE="/api/admin/reports/sam?take=200&status=$2" ;;
      *) echo "STOP: the filter must be attention, all, new, reviewed, accepted, deferred or dismissed" >&2; exit 2 ;;
    esac ;;
  sam-show) [ $# -eq 2 ] || usage; check_id "$2"; ROUTE="/api/admin/reports/sam/$2" ;;
  sam-exchange) [ $# -eq 2 ] || usage; check_id "$2"; METHOD=POST; ROUTE="/api/admin/reports/sam/$2/exchange" ;;
  sam-triage)
    [ $# -eq 4 ] || [ $# -eq 5 ] || usage
    check_id "$2"
    case "$3" in
      ""|*[!0-9]*|0*) echo "STOP: flagCount must be the report's flagCount as you read it (a whole number)" >&2; exit 2 ;;
    esac
    METHOD=POST; ROUTE="/api/admin/reports/sam/$2/triage"
    BODY="{\"seenFlagCount\":$3"
    case "$4" in
      keep) ;;
      new|reviewed|accepted|deferred|dismissed) BODY="$BODY,\"status\":\"$4\"" ;;
      *) echo "STOP: status must be keep, new, reviewed, accepted, deferred or dismissed" >&2; exit 2 ;;
    esac
    if [ $# -eq 5 ]; then
      case "$5" in
        none) BODY="$BODY,\"unlinkIssue\":true" ;;
        ""|*[!0-9]*|0*) echo "STOP: the issue must be a whole number, or none to unlink" >&2; exit 2 ;;
        *) BODY="$BODY,\"issueNumber\":$5" ;;
      esac
    fi
    BODY="$BODY}" ;;
  sam-counts)
    [ $# -le 2 ] || usage
    ROUTE=/api/admin/reports/sam/counts
    if [ $# -eq 2 ]; then
      case "$2" in
        [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]) ROUTE="$ROUTE?since=$2" ;;
        *) echo "STOP: sinceDate must be a UTC day as yyyy-mm-dd" >&2; exit 2 ;;
      esac
    fi ;;
  github) [ $# -eq 1 ] || usage; ROUTE="/api/admin/reports/github?take=200" ;;
  screen-state) [ $# -eq 2 ] || usage; check_id "$2"; METHOD=POST; ROUTE="/api/admin/reports/github/screen-states/$2" ;;
  *) usage ;;
esac

# Which token goes where is decided here, before anything is read or sent. A token is a way to
# sign in: the production one must never reach another host, nor a local one reach production.
if [ "$BASE" = "$PROD" ]; then
  if [ -n "${ADMIN_AGENT_TOKEN:-}" ]; then
    echo "STOP: ADMIN_AGENT_TOKEN is set and the API is production. Production takes only the Key Vault token: unset ADMIN_AGENT_TOKEN, or point ADMIN_API_BASE at the local stack." >&2
    exit 2
  fi
  if ! TOKEN="$(az keyvault secret show --vault-name "$VAULT" --name "$SECRET" --query value -o tsv 2>/dev/null)" \
     || [ -z "$TOKEN" ]; then
    echo "STOP: no agent token. Key Vault $VAULT has no readable secret $SECRET (is az signed in?)." >&2
    exit 2
  fi
else
  TOKEN="${ADMIN_AGENT_TOKEN:-}"
  if [ -z "$TOKEN" ]; then
    echo "STOP: ADMIN_API_BASE is set, so ADMIN_AGENT_TOKEN must be too. The production token is not sent to another API." >&2
    exit 2
  fi
fi
case "$TOKEN" in
  ssadm_*) ;;
  *) echo "STOP: what was read is not an agent token (it does not start with ssadm_)." >&2; exit 2 ;;
esac

OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT

# The header is read from a pipe, so the token is in no process's arguments and no file.
ARGS=(--silent --show-error --max-time 30 --output "$OUT" --write-out '%{http_code}' --request "$METHOD"
      --header @/dev/fd/3 --header 'Accept: application/json')
if [ -n "$BODY" ]; then
  ARGS+=(--header 'Content-Type: application/json' --data "$BODY")
elif [ "$METHOD" = POST ]; then
  ARGS+=(--header 'Content-Length: 0')
fi

if ! STATUS="$(curl "${ARGS[@]}" "$BASE$ROUTE" 3< <(printf 'X-Admin-Token: %s\n' "$TOKEN"))"; then
  echo "FAIL: $METHOD ${ROUTE%%\?*} could not be sent to $BASE" >&2
  exit 1
fi

if [ "$STATUS" = 200 ]; then
  cat "$OUT"
  echo
  exit 0
fi

CODE="$(python3 -I -c 'import json,sys
try:
    print(json.load(open(sys.argv[1])).get("code") or "")
except Exception:
    print("")' "$OUT")"
case "$STATUS" in
  404) echo "FAIL: 404 from $METHOD ${ROUTE%%\?*}: the token is not accepted (expired, revoked, or its creator is no longer an admin or changed their password), or there is nothing at that id." >&2 ;;
  403) echo "FAIL: 403 ${CODE:-refused} from $METHOD ${ROUTE%%\?*}: the token lacks the permission this needs." >&2 ;;
  *) echo "FAIL: $STATUS ${CODE:-} from $METHOD ${ROUTE%%\?*}" >&2 ;;
esac
exit 1
