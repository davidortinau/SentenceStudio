#!/bin/bash
# Temporary access to the production PostgreSQL server, and the Step 1 backup from
# docs/deploy-runbook.md. Every subcommand except status changes Azure: run it only
# with the Captain's go for that step.
#
#   scripts/prod-db-access.sh status   firewall rules and locks (read-only)
#   scripts/prod-db-access.sh open     credentials file + a firewall rule for this IP
#   scripts/prod-db-access.sh close    delete that rule (lifting the server lock for about
#                                      a minute), then check the lock and rules
#   scripts/prod-db-access.sh backup   open, dump, verify, record row counts, close
#
# While open, $STATE/pg.env holds PGHOST/PGUSER/PGPASSWORD (mode 600) for
#   docker run --rm --env-file "$STATE/pg.env" postgres:16 psql -d sentencestudio ...
# Never print it or put the password on a command line. Queries are read-only unless the
# Captain has approved a write.
#
# Run this script directly, not from another script's EXIT trap: macOS bash 3.2 then skips
# the lock-restoring trap in close (it happened on 2026-11-05).
set -euo pipefail

S=66f9fa8f-604f-4688-bec1-16ff9a86a8e5
RG=rg-sstudio-prod-biz
SERVER=db-rsn72awybem6s
VAULT=dbkv-rsn72awybem6s
PG=postgres:16
STATE="$HOME/.sstudio-prod-db-access"
# Newer az CLIs (2.92 on Lilliput) name the server --server-name and the rule --name; older
# ones used --name for the server and --rule-name for the rule.
# Read the help first: under pipefail, grep -q closing the pipe early could fail the test.
FW_HELP=$(az postgres flexible-server firewall-rule list -h 2>/dev/null || true)
if grep -q -- '--server-name' <<<"$FW_HELP"; then
  FW_SERVER=(--server-name "$SERVER"); FW_RULE=--name
else
  FW_SERVER=(--name "$SERVER"); FW_RULE=--rule-name
fi
LOCK_ARGS=(-n do-not-delete-postgres -g "$RG" --resource "$SERVER"
  --resource-type Microsoft.DBforPostgreSQL/flexibleServers --subscription "$S")

status() {
  echo "## firewall rules"
  az postgres flexible-server firewall-rule list -g "$RG" "${FW_SERVER[@]}" --subscription "$S" \
    --query '[].name' -o tsv
  echo "## locks"
  az lock list -g "$RG" --subscription "$S" --query '[].{name:name, level:level}' -o tsv
}

open_access() {
  if [ -f "$STATE/rule" ]; then
    echo "STOP: access is already open (rule $(cat "$STATE/rule")); run close first"; exit 1
  fi
  mkdir -p "$STATE"; chmod 700 "$STATE"
  (
    umask 077
    # The API's secret; connectionstrings--db holds an old password.
    az keyvault secret show --vault-name "$VAULT" -n connectionstrings--sentencestudio \
      --query value -o tsv > "$STATE/conn.txt"
    python3 -I - "$STATE/conn.txt" > "$STATE/pg.env" <<'PY'
import sys
p = dict(x.split("=", 1) for x in open(sys.argv[1]).read().strip().split(";") if "=" in x)
print(f"PGHOST={p['Host']}\nPGUSER={p['Username']}\nPGPASSWORD={p['Password']}\nPGSSLMODE=require")
PY
    rm -f "$STATE/conn.txt"
  )
  grep -q '^PGPASSWORD=.' "$STATE/pg.env" || { rm -f "$STATE/pg.env"; echo "FAIL: no credentials read"; exit 1; }
  local rule ip
  rule=deploy-access-$(date +%Y%m%d-%H%M)
  ip=$(curl -s https://api.ipify.org)
  echo "$rule" > "$STATE/rule"
  az postgres flexible-server firewall-rule create -g "$RG" "${FW_SERVER[@]}" --subscription "$S" \
    "$FW_RULE" "$rule" --start-ip-address "$ip" --end-ip-address "$ip" -o none
  echo "open: rule $rule for this IP; credentials in $STATE/pg.env. Run close when done."
}

close_access() {
  if [ -f "$STATE/rule" ]; then
    local rule; rule=$(cat "$STATE/rule")
    # The CanNotDelete lock also blocks deleting firewall rules: lift it, delete the rule,
    # and put it back. The subshell's EXIT trap restores the lock even if the delete fails.
    (
      trap 'az lock create "${LOCK_ARGS[@]}" -t CanNotDelete \
        --notes "Protect production managed PostgreSQL from accidental deletion" -o none' EXIT
      az lock delete "${LOCK_ARGS[@]}" &&
      az postgres flexible-server firewall-rule delete -g "$RG" "${FW_SERVER[@]}" --subscription "$S" \
        "$FW_RULE" "$rule" --yes
    ) || true
  fi
  rm -f "$STATE/pg.env" "$STATE/conn.txt"

  local locks rules
  locks=$(az lock list -g "$RG" --subscription "$S" --query "[?name=='do-not-delete-postgres'].level" -o tsv)
  if [ "$locks" != CanNotDelete ]; then
    echo "lock missing; recreating it"
    az lock create "${LOCK_ARGS[@]}" -t CanNotDelete \
      --notes "Protect production managed PostgreSQL from accidental deletion" -o none
  fi
  rules=$(az postgres flexible-server firewall-rule list -g "$RG" "${FW_SERVER[@]}" --subscription "$S" \
    --query '[].name' -o tsv)
  if [ "$rules" = AllowAllAzureIps ]; then
    rm -f "$STATE/rule"
    echo "closed: only AllowAllAzureIps remains; lock do-not-delete-postgres is CanNotDelete"
  else
    echo "FAIL: firewall rules left: $(echo $rules); rerun close"; exit 1
  fi
}

backup() {
  local dir; dir="$HOME/sstudio-prod-backup-$(date +%Y%m%d-%H%M)"
  mkdir -p "$dir"; chmod 700 "$dir"
  open_access
  # No EXIT trap here (see the header); close runs whatever happens to the dump.
  set +e
  (
    set -e
    docker run --rm --env-file "$STATE/pg.env" -v "$dir":/out "$PG" \
      pg_dump -d sentencestudio -Fc -f /out/sentencestudio.dump
    docker run --rm -v "$dir":/in "$PG" pg_restore -l /in/sentencestudio.dump > "$dir/toc.txt"
    docker run --rm --env-file "$STATE/pg.env" "$PG" psql -d sentencestudio -At -c \
      'select (select count(*) from "VocabularyWord"), (select count(*) from "VocabularyProgress"), (select count(*) from "UserProfile")' \
      > "$dir/row-counts.txt"
  )
  local dumped=$?
  set -e
  close_access
  [ $dumped = 0 ] || { echo "FAIL: the dump failed; do not deploy a migration or data change"; exit 1; }

  local tables data
  tables=$(grep -c ' TABLE public ' "$dir/toc.txt" || true)
  data=$(grep -c ' TABLE DATA public ' "$dir/toc.txt" || true)
  if [ "$tables" -gt 0 ] && [ "$tables" = "$data" ]; then
    echo "backup: $dir/sentencestudio.dump ($(du -h "$dir/sentencestudio.dump" | cut -f1)), $data of $tables tables"
    echo "counts VocabularyWord|VocabularyProgress|UserProfile = $(cat "$dir/row-counts.txt")"
  else
    echo "FAIL: the dump lists $data table data entries for $tables tables"; exit 1
  fi
}

case "${1:-}" in
  status) status ;;
  open) open_access ;;
  close) close_access ;;
  backup) backup ;;
  *) echo "usage: $0 status|open|close|backup" >&2; exit 64 ;;
esac
