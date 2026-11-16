#!/bin/bash
# Calls the admin API as the maintenance agent, with the agent token (docs/admin-api.md).
#
#   scripts/admin-api.sh whoami                          the token: id, permissions, when it expires
#   scripts/admin-api.sh sam [status]                    flagged Sam answers; status is new, reviewed,
#                                                        accepted, deferred or dismissed (default: all)
#   scripts/admin-api.sh sam-show <reportId>             one report and each flag on it, in codes
#   scripts/admin-api.sh sam-exchange <reportId>         the flagged question and answer (LEARNER CONTENT)
#   scripts/admin-api.sh sam-triage <reportId> <status> [issueNumber]
#                                                        set the status and the linked GitHub issue;
#                                                        leaving the number out unlinks it
#   scripts/admin-api.sh github                          reports filed to GitHub from the app
#   scripts/admin-api.sh screen-state <reference>        a GitHub report's saved screen state (LEARNER CONTENT)
#
# Prints the API's JSON on stdout and exits 0 for a 200. Anything else prints the status and the
# API's failure code on stderr and exits 1 (2 for a usage or token problem), so a caller can
# never mistake a refusal for an empty inbox.
#
# The token comes from ADMIN_AGENT_TOKEN when it is set (a local stack), otherwise from Key Vault
# (dbkv-rsn72awybem6s, secret admin-agent-token) through `az`, which must be signed in. Reading
# that one secret is covered by the Captain's standing go (docs/admin-portal-plan.md); nothing
# else in Azure is. The API is ADMIN_API_BASE when set, otherwise production. The token is never
# printed and never placed on a command line.
set -euo pipefail

VAULT=dbkv-rsn72awybem6s
SECRET=admin-agent-token
BASE="${ADMIN_API_BASE:-https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io}"
BASE="${BASE%/}"

usage() { sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

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
    case "${2:-}" in
      "") ROUTE="/api/admin/reports/sam?take=200" ;;
      new|reviewed|accepted|deferred|dismissed) ROUTE="/api/admin/reports/sam?take=200&status=$2" ;;
      *) echo "STOP: status must be new, reviewed, accepted, deferred or dismissed" >&2; exit 2 ;;
    esac ;;
  sam-show) [ $# -eq 2 ] || usage; check_id "$2"; ROUTE="/api/admin/reports/sam/$2" ;;
  sam-exchange) [ $# -eq 2 ] || usage; check_id "$2"; METHOD=POST; ROUTE="/api/admin/reports/sam/$2/exchange" ;;
  sam-triage)
    [ $# -eq 3 ] || [ $# -eq 4 ] || usage
    check_id "$2"
    case "$3" in
      new|reviewed|accepted|deferred|dismissed) ;;
      *) echo "STOP: status must be new, reviewed, accepted, deferred or dismissed" >&2; exit 2 ;;
    esac
    ISSUE=null
    if [ $# -eq 4 ]; then
      case "$4" in
        ""|*[!0-9]*|0*) echo "STOP: the issue number must be a whole number" >&2; exit 2 ;;
      esac
      ISSUE="$4"
    fi
    METHOD=POST; ROUTE="/api/admin/reports/sam/$2/triage"
    BODY="{\"status\":\"$3\",\"issueNumber\":$ISSUE}" ;;
  github) [ $# -eq 1 ] || usage; ROUTE="/api/admin/reports/github?take=200" ;;
  screen-state) [ $# -eq 2 ] || usage; check_id "$2"; METHOD=POST; ROUTE="/api/admin/reports/github/screen-states/$2" ;;
  *) usage ;;
esac

TOKEN="${ADMIN_AGENT_TOKEN:-}"
if [ -z "$TOKEN" ]; then
  if ! TOKEN="$(az keyvault secret show --vault-name "$VAULT" --name "$SECRET" --query value -o tsv 2>/dev/null)" \
     || [ -z "$TOKEN" ]; then
    echo "STOP: no agent token. Key Vault $VAULT has no readable secret $SECRET (is az signed in?), and ADMIN_AGENT_TOKEN is not set." >&2
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
  404) echo "FAIL: 404 from $METHOD ${ROUTE%%\?*}: the token is not accepted (missing, expired, revoked, or its creator is no longer an admin), or there is nothing at that id." >&2 ;;
  403) echo "FAIL: 403 ${CODE:-refused} from $METHOD ${ROUTE%%\?*}: the token lacks the permission this needs." >&2 ;;
  *) echo "FAIL: $STATUS ${CODE:-} from $METHOD ${ROUTE%%\?*}" >&2 ;;
esac
exit 1
