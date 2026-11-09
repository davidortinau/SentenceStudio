#!/bin/bash
# Production deploy, Option A of docs/deploy-runbook.md (azd deploy), with its checks.
#
#   scripts/prod-deploy.sh check    read-only pre-checks; changes nothing
#   scripts/prod-deploy.sh deploy   check, then azd deploy with the live Sam (Coach__*)
#                                   settings and the feedback key, then after
#   scripts/prod-deploy.sh after    revisions healthy, sticky sessions re-applied (azd resets
#                                   them), Sam settings equal the previous revision's, and
#                                   scripts/post-deploy-validate.sh --skip-wait
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

check() {
  local fails=0
  fail() { echo "FAIL: $*"; fails=$((fails + 1)); }
  ok() { echo "ok:   $*"; }

  [ "$(az account show --query id -o tsv)" = "$S" ] && ok "subscription" \
    || fail "az is not on subscription $S (az account set -s $S)"
  [ -e .azure/sstudio-prod-biz ] && ok "azd environment" \
    || fail "no .azure here; ln -s /Users/davidortinau/work/SentenceStudio/.azure .azure"
  if [ -z "$(git status --porcelain --untracked-files=no)" ] \
     && [ -z "$(git status --porcelain -- src | grep '^??' || true)" ]; then
    ok "clean checkout at $(git rev-parse --short HEAD) ($(git rev-parse --abbrev-ref HEAD))"
  else
    fail "uncommitted changes would ship; commit them or deploy from a clean checkout"
  fi

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

  ./scripts/post-deploy-validate.sh --skip-wait || fails=$((fails + 1))
  if [ $fails -gt 0 ]; then echo "FAIL: $fails post-deploy check(s) failed"; return 1; fi
  echo "post-deploy checks passed; now do the change-specific checks (runbook Step 6)"
}

case "${1:-}" in
  check) check ;;
  deploy) deploy ;;
  after) after ;;
  *) echo "usage: $0 check|deploy|after" >&2; exit 64 ;;
esac
