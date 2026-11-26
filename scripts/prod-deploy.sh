#!/bin/bash
# Production deploy, Option A of docs/deploy-runbook.md (azd deploy), with its checks.
#
#   scripts/prod-deploy.sh check    read-only pre-checks; changes nothing
#   scripts/prod-deploy.sh deploy   check, then azd deploy with the live Sam (Coach__*)
#                                   settings, the live admin list (Admin__*) and the
#                                   feedback key, then after
#   scripts/prod-deploy.sh after    revisions healthy, sticky sessions re-applied (azd resets
#                                   them), Sam and admin settings equal the previous
#                                   revision's, scripts/post-deploy-validate.sh --skip-wait,
#                                   and, when all of that passed, one line for this deploy in
#                                   docs/deploy-log.md
#
# The deploy log (docs/deploy-log.md): the one record of what production ran and since when.
# after appends the line and does not commit it: commit and push it, because a reader finds
# it on main. check does not count that file as an uncommitted change (it does not ship).
#
# The admin list (Admin__Emails__N, docs/admin-portal-plan.md): azd rebuilds the API's settings
# from this shell on every deploy, so the live list is read and carried forward, as Sam's
# settings are. Without that each deploy would empty it, and an empty list means nobody is an
# admin. While production has no list yet, and only then, Admin__Emails__N set in this shell
# becomes the list: that is how the first address is put there, with the Captain's go. To
# change a list that exists, change it on the container app, not here.
#
# Azure commands need the Captain's go, including check. deploy ships this checkout's
# working tree, so it stops on uncommitted tracked changes. It does not take the database
# backup (Step 1): run scripts/prod-db-access.sh backup first when the deploy carries a
# migration or a data change. Turn VPN off (management.azure.com times out on it).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
S=66f9fa8f-604f-4688-bec1-16ff9a86a8e5
RG=rg-sstudio-prod-biz
SERVER=db-rsn72awybem6s
VAULT=dbkv-rsn72awybem6s
COACH_QUERY="properties.template.containers[0].env[?starts_with(name,'Coach__')]"
ADMIN_QUERY="properties.template.containers[0].env[?starts_with(name,'Admin__')]"
LOG=docs/deploy-log.md

check() {
  local fails=0
  fail() { echo "FAIL: $*"; fails=$((fails + 1)); }
  ok() { echo "ok:   $*"; }

  [ "$(az account show --query id -o tsv)" = "$S" ] && ok "subscription" \
    || fail "az is not on subscription $S (az account set -s $S)"
  if [ -L .azure ]; then
    fail "azd refuses a symlinked .azure; rm .azure && cp -Rp /Users/davidortinau/work/SentenceStudio/.azure .azure"
  elif [ -e .azure/sstudio-prod-biz ]; then
    ok "azd environment"
  else
    fail "no .azure here; cp -Rp /Users/davidortinau/work/SentenceStudio/.azure .azure"
  fi
  if [ -z "$(git status --porcelain --untracked-files=no -- . ":(exclude)$LOG")" ] \
     && [ -z "$(git status --porcelain -- src | grep '^??' || true)" ]; then
    ok "clean checkout at $(git rev-parse --short HEAD) ($(git rev-parse --abbrev-ref HEAD))"
  else
    fail "uncommitted changes would ship; commit them or deploy from a clean checkout"
  fi
  [ -z "$(git status --porcelain -- "$LOG")" ] \
    || echo "note: $LOG holds a deploy line that is not committed; commit and push it"

  local state lock
  state=$(az postgres flexible-server show -g "$RG" -n "$SERVER" --subscription "$S" --query state -o tsv)
  [ "$state" = Ready ] && ok "database server Ready" || fail "database server is '$state'"
  lock=$(az lock list -g "$RG" --subscription "$S" --query "[?name=='do-not-delete-postgres'].level" -o tsv)
  [ "$lock" = CanNotDelete ] && ok "database lock" \
    || fail "lock do-not-delete-postgres missing (runbook Step 3)"

  local names secrets
  names=$(az containerapp show -g "$RG" -n api --subscription "$S" --query "$COACH_QUERY.name" -o tsv | grep -c . || true)
  secrets=$(az containerapp show -g "$RG" -n api --subscription "$S" \
    --query "length($COACH_QUERY | [?secretRef])" -o tsv)
  if [ "$names" -gt 0 ] && [ "$secrets" = 0 ]; then ok "$names Sam settings, all plain values"
  else fail "$names Sam settings, $secrets stored as secrets (update the runbook block first)"; fi
  local admins admin_secrets
  admins=$(az containerapp show -g "$RG" -n api --subscription "$S" --query "$ADMIN_QUERY.name" -o tsv | grep -c . || true)
  admin_secrets=$(az containerapp show -g "$RG" -n api --subscription "$S" \
    --query "length($ADMIN_QUERY | [?secretRef])" -o tsv)
  if [ "$admin_secrets" != 0 ]; then
    fail "$admin_secrets admin settings stored as secrets; they cannot be carried forward"
  elif [ "$admins" -gt 0 ]; then
    ok "$admins admin settings live; they will be carried forward"
  else
    ok "no admin list live: $(env | grep -c '^Admin__Emails__' || true) address(es) in this shell will become it (0 leaves the admin portal closed)"
  fi
  [ -n "$(az keyvault secret show --vault-name "$VAULT" -n feedback-hmac-key --query value -o tsv)" ] \
    && ok "feedback key in Key Vault" || fail "feedback-hmac-key missing"
  [ -n "$(az keyvault secret show --vault-name "$VAULT" -n github-app-private-key --query value -o tsv)" ] \
    && ok "GitHub App key in Key Vault" || fail "github-app-private-key missing (feedback issues cannot be filed)"

  if [ $fails -gt 0 ]; then echo "STOP: $fails pre-check(s) failed; do not deploy"; return 1; fi
  echo "pre-checks passed"
}

deploy() {
  check
  # The runbook's Option A block. The subshell keeps the Coach__* values and the
  # feedback key out of this shell, and azd runs only if both checks pass.
  (
    for name in $(env | sed -n 's/^\(Coach__[^=]*\)=.*/\1/p'); do unset "$name"; done
    LIVE_NAMES=$(az containerapp show -g "$RG" -n api --subscription "$S" \
      --query "$COACH_QUERY.name" -o tsv | sort)
    eval "$(az containerapp show -g "$RG" -n api --subscription "$S" --query "$COACH_QUERY" -o json \
      | python3 -I -c 'import json,shlex,sys; [print("export %s=%s" % (e["name"], shlex.quote(e["value"]))) for e in json.load(sys.stdin) if "value" in e]')"
    EXPORTED_NAMES=$(env | sed -n 's/^\(Coach__[^=]*\)=.*/\1/p' | sort)
    echo "Sam settings live: $(echo "$LIVE_NAMES" | grep -c .) exported: $(echo "$EXPORTED_NAMES" | grep -c .)"

    # The admin list. A live list replaces whatever this shell holds; with none live, what
    # this shell holds (possibly nothing) is what ships.
    LIVE_ADMIN_NAMES=$(az containerapp show -g "$RG" -n api --subscription "$S" \
      --query "$ADMIN_QUERY.name" -o tsv | sort)
    if [ -n "$LIVE_ADMIN_NAMES" ]; then
      for name in $(env | sed -n 's/^\(Admin__[^=]*\)=.*/\1/p'); do unset "$name"; done
      eval "$(az containerapp show -g "$RG" -n api --subscription "$S" --query "$ADMIN_QUERY" -o json \
        | python3 -I -c 'import json,shlex,sys; [print("export %s=%s" % (e["name"], shlex.quote(e["value"]))) for e in json.load(sys.stdin) if "value" in e]')"
    fi
    EXPORTED_ADMIN_NAMES=$(env | sed -n 's/^\(Admin__[^=]*\)=.*/\1/p' | sort)
    echo "Admin settings live: $(echo "$LIVE_ADMIN_NAMES" | grep -c . || true) exported: $(echo "$EXPORTED_ADMIN_NAMES" | grep -c . || true)"
    if [ -n "$LIVE_ADMIN_NAMES" ] && [ "$LIVE_ADMIN_NAMES" != "$EXPORTED_ADMIN_NAMES" ]; then
      echo "STOP: a live admin setting could not be carried forward; do not deploy"; exit 1
    fi
    FEEDBACK_KEY="$(az keyvault secret show --vault-name "$VAULT" -n feedback-hmac-key --query value -o tsv)"
    GITHUB_APP_KEY="$(az keyvault secret show --vault-name "$VAULT" -n github-app-private-key --query value -o tsv)"
    if [ -n "$FEEDBACK_KEY" ] && [ -n "$GITHUB_APP_KEY" ] && [ -n "$LIVE_NAMES" ] && [ "$LIVE_NAMES" = "$EXPORTED_NAMES" ]; then
      AZURE_FEEDBACKHMACKEY="$FEEDBACK_KEY" AZURE_GITHUBAPPPRIVATEKEY="$GITHUB_APP_KEY" \
        azd deploy -e sstudio-prod-biz --no-prompt
    else
      echo "STOP: the feedback key, the GitHub App key or a Coach__ setting is missing; do not deploy"; exit 1
    fi
  )
  after
}

wait_healthy() {
  local app=$1 rev health running _
  for _ in $(seq 1 30); do
    rev=$(az containerapp show -g "$RG" -n "$app" --subscription "$S" --query properties.latestRevisionName -o tsv)
    health=$(az containerapp revision show -g "$RG" -n "$app" --revision "$rev" --subscription "$S" \
      --query properties.healthState -o tsv 2>/dev/null || true)
    running=$(az containerapp revision show -g "$RG" -n "$app" --revision "$rev" --subscription "$S" \
      --query properties.runningState -o tsv 2>/dev/null || true)
    [ "$health" = Healthy ] && [ "$running" = Running ] && { echo "ok:   $app $rev Healthy, Running"; return 0; }
    sleep 10
  done
  echo "FAIL: $app $rev is '$health', '$running' after 5 minutes"; return 1
}

# One line in docs/deploy-log.md for the deploy that made the API revision $1: when that
# revision was created (Azure's time, in UTC), this checkout's commit and its subject, and
# the revision's name. A revision already in the log is not written twice, so after can be
# rerun. Nothing is committed.
record_deploy() {
  local revision=$1 created when epoch commit committed line
  [ -f "$LOG" ] || { echo "FAIL: $LOG is missing; this deploy is not recorded"; return 1; }
  if grep -qF "\`$revision\`" "$LOG"; then
    echo "ok:   $revision is already in $LOG"; return 0
  fi
  created=$(az containerapp revision show -g "$RG" -n api --revision "$revision" --subscription "$S" \
    --query properties.createdTime -o tsv)
  read -r epoch when < <(python3 -I -c 'import re, sys
from datetime import datetime, timezone
try:
    t = datetime.fromisoformat(re.sub(r"\.\d+", "", sys.argv[1].strip()).replace("Z", "+00:00"))
    if t.tzinfo is None: t = t.replace(tzinfo=timezone.utc)
    print(int(t.timestamp()), t.astimezone(timezone.utc).strftime("%Y-%m-%d %H:%M UTC"))
except ValueError:
    print()' "$created") || true
  commit=$(git rev-parse HEAD)
  line="- $when \`$commit\` $(git log -1 --format=%s HEAD) (API revision \`$revision\`)"
  if [ -z "${epoch:-}" ]; then
    echo "FAIL: Azure gave no creation time for $revision ('$created'); this deploy is not recorded."
    echo "      Add it to $LOG by hand, with the time: $line"
    return 1
  fi
  # A commit made after the revision existed cannot be what it runs: the checkout has moved
  # since the deploy (after rerun later, from another checkout or after a pull).
  committed=$(git log -1 --format=%ct HEAD)
  if [ "$committed" -gt "$epoch" ]; then
    echo "FAIL: $revision was created $when, before this checkout's commit $(git rev-parse --short HEAD) was made,"
    echo "      so that commit is not what was deployed; this deploy is not recorded."
    echo "      Add it to $LOG by hand, with the commit that was deployed: $line"
    return 1
  fi
  # The log may have lost its final newline to an editor; a line is never joined to the last.
  [ -z "$(tail -c 1 "$LOG")" ] || echo >> "$LOG"
  echo "$line" >> "$LOG"
  echo "ok:   recorded in $LOG (not committed; commit and push it):"
  echo "      $line"
}

after() {
  local fails=0 app
  for app in api webapp workers marketing; do wait_healthy "$app" || fails=$((fails + 1)); done

  az containerapp ingress sticky-sessions set -n webapp -g "$RG" --subscription "$S" --affinity sticky -o none
  [ "$(az containerapp show -n webapp -g "$RG" --subscription "$S" \
      --query properties.configuration.ingress.stickySessions.affinity -o tsv)" = sticky ] \
    && echo "ok:   webapp sticky sessions" || { echo "FAIL: webapp sticky sessions"; fails=$((fails + 1)); }

  # Sam settings: names and values must equal the revision before this one. Only
  # fingerprints and names are printed, never values.
  local revs prev latest q a b
  revs=$(az containerapp revision list -g "$RG" -n api --subscription "$S" --all \
    --query "sort_by([], &properties.createdTime)[-2:].name" -o tsv)
  prev=$(echo "$revs" | head -1); latest=$(echo "$revs" | tail -1)
  q='properties.template.containers[0].env[?starts_with(name,`Coach__`)].[name,value]'
  a=$(az containerapp revision show -g "$RG" -n api --revision "$prev" --subscription "$S" --query "$q" -o tsv | sort | shasum | cut -c1-12)
  b=$(az containerapp revision show -g "$RG" -n api --revision "$latest" --subscription "$S" --query "$q" -o tsv | sort | shasum | cut -c1-12)
  if [ "$a" = "$b" ]; then
    echo "ok:   Sam settings in $latest equal $prev ($b)"
  else
    echo "FAIL: Sam settings in $latest differ from $prev; names in each:"
    diff <(az containerapp revision show -g "$RG" -n api --revision "$prev" --subscription "$S" --query "$COACH_QUERY.name" -o tsv | sort) \
         <(az containerapp revision show -g "$RG" -n api --revision "$latest" --subscription "$S" --query "$COACH_QUERY.name" -o tsv | sort) || true
    fails=$((fails + 1))
  fi

  # The admin list: equal to the previous revision's when that had one. Fingerprints and a
  # count only.
  local aq pa pb pn
  aq='properties.template.containers[0].env[?starts_with(name,`Admin__`)].[name,value]'
  pn=$(az containerapp revision show -g "$RG" -n api --revision "$prev" --subscription "$S" --query "$aq" -o tsv | grep -c . || true)
  pa=$(az containerapp revision show -g "$RG" -n api --revision "$prev" --subscription "$S" --query "$aq" -o tsv | sort | shasum | cut -c1-12)
  pb=$(az containerapp revision show -g "$RG" -n api --revision "$latest" --subscription "$S" --query "$aq" -o tsv | sort | shasum | cut -c1-12)
  if [ "$pn" = 0 ]; then
    echo "ok:   $prev had no admin list; $latest has $(az containerapp revision show -g "$RG" -n api --revision "$latest" --subscription "$S" --query "$aq" -o tsv | grep -c . || true) admin setting(s)"
  elif [ "$pa" = "$pb" ]; then
    echo "ok:   admin settings in $latest equal $prev ($pb)"
  else
    echo "FAIL: admin settings in $latest differ from $prev"; fails=$((fails + 1))
  fi

  ./scripts/post-deploy-validate.sh --skip-wait || fails=$((fails + 1))
  if [ $fails -gt 0 ]; then echo "FAIL: $fails post-deploy check(s) failed; this deploy is not recorded in $LOG (rerun after once they pass)"; return 1; fi
  record_deploy "$latest" || { echo "post-deploy checks passed, but the deploy is not in $LOG (above)"; return 1; }
  echo "post-deploy checks passed; now do the change-specific checks (runbook Step 6)"
}

case "${1:-}" in
  check) check ;;
  deploy) deploy ;;
  after) after ;;
  *) echo "usage: $0 check|deploy|after" >&2; exit 64 ;;
esac
