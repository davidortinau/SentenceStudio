#!/bin/bash
# Grants or removes the Admin role, the database half of being an admin (docs/admin-portal-plan.md).
# The other half is the account's email on the API's Admin:Emails setting; either alone gives
# nothing. The app has no way to grant the role: this script is the only one.
#
#   scripts/admin-grant.sh <email>               grant, in the local stack's database container
#   scripts/admin-grant.sh <email> --revoke      remove the role from that account
#   scripts/admin-grant.sh --list                who holds the role (read-only)
#   ... --container <name>                       a specific local PostgreSQL container
#   ... --prod                                   production, while scripts/prod-db-access.sh
#                                                open is in effect. --list is read-only.
#   ... --prod --yes                             a production database WRITE (grant or revoke).
#                                                Pass --yes only after the Captain approves
#                                                that write.
#
# The account must already exist; this never creates one. It prints the account's id, never a
# password or a connection string.
set -euo pipefail

usage() { sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-1}"; }

EMAIL=""; MODE=grant; TARGET=local; CONTAINER=""; YES=0
while [ $# -gt 0 ]; do
  case "$1" in
    --revoke) MODE=revoke ;;
    --list) MODE=list ;;
    --prod) TARGET=prod ;;
    --yes) YES=1 ;;
    --container) shift; CONTAINER="${1:-}"; [ -n "$CONTAINER" ] || usage ;;
    -h|--help) usage 0 ;;
    -*) echo "unknown option: $1"; usage ;;
    *) [ -z "$EMAIL" ] || usage; EMAIL="$1" ;;
  esac
  shift
done
if [ "$MODE" != list ]; then
  # One address, no whitespace or quotes: it travels to psql as a variable, never as SQL text.
  case "$EMAIL" in
    ""|*[[:space:]\'\"\\]*) echo "STOP: give one email address"; usage ;;
    *@*) ;;
    *) echo "STOP: '$EMAIL' is not an email address"; exit 1 ;;
  esac
fi

if [ "$TARGET" = prod ] && [ "$MODE" != list ] && [ "$YES" != 1 ]; then
  echo "STOP: this would $MODE the Admin role in the PRODUCTION database."
  echo "It is a production write: get the Captain's go for it, then add --yes."
  exit 1
fi

PATH="$HOME/.docker/bin:$PATH"
PROD_STATE="$HOME/.sstudio-prod-db-access"

# Runs psql against the chosen database with the SQL on stdin. Credentials stay in the
# container's own environment (local) or in the env file prod-db-access.sh wrote (production).
run_psql() {
  if [ "$TARGET" = prod ]; then
    [ -f "$PROD_STATE/pg.env" ] || {
      echo "STOP: production access is not open. With the Captain's go: scripts/prod-db-access.sh open"; exit 1; }
    docker run --rm -i --env-file "$PROD_STATE/pg.env" postgres:16 \
      psql -X -q -At -v ON_ERROR_STOP=1 -d sentencestudio "$@"
  else
    docker exec -i "$CONTAINER" sh -c \
      'PGPASSWORD="$POSTGRES_PASSWORD" exec psql -X -q -At -v ON_ERROR_STOP=1 -h 127.0.0.1 -U "${POSTGRES_USER:-postgres}" -d sentencestudio "$@"' \
      psql "$@"
  fi
}

if [ "$TARGET" = local ] && [ -z "$CONTAINER" ]; then
  FOUND=$(docker ps --format '{{.Names}}' | grep -E '^db-' || true)
  if [ "$(printf '%s\n' "$FOUND" | grep -c .)" != 1 ]; then
    echo "STOP: expected one running local database container named db-*; found: ${FOUND:-none}."
    echo "Name it with --container <name> (docker ps)."
    exit 1
  fi
  CONTAINER="$FOUND"
fi
echo "database: $([ "$TARGET" = prod ] && echo PRODUCTION || echo "local container $CONTAINER")"

if [ "$MODE" = list ]; then
  echo "accounts holding the Admin role (id | email | email confirmed):"
  run_psql <<'SQL'
select u."Id" || ' | ' || coalesce(u."Email", '') || ' | ' || u."EmailConfirmed"
from "AspNetUsers" u
join "AspNetUserRoles" ur on ur."UserId" = u."Id"
join "AspNetRoles" r on r."Id" = ur."RoleId"
where r."NormalizedName" = 'ADMIN'
order by u."Email";
SQL
  exit 0
fi

MATCHES=$(run_psql -v email="$EMAIL" <<'SQL'
select count(*) from "AspNetUsers" where "NormalizedEmail" = upper(:'email');
SQL
)
if [ "$MATCHES" != 1 ]; then
  echo "STOP: $MATCHES accounts have that email; expected exactly one. Nothing was changed."
  exit 1
fi

if [ "$MODE" = grant ]; then
  run_psql -v email="$EMAIL" <<'SQL'
begin;
insert into "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
select gen_random_uuid()::text, 'Admin', 'ADMIN', gen_random_uuid()::text
where not exists (select 1 from "AspNetRoles" where "NormalizedName" = 'ADMIN');
insert into "AspNetUserRoles" ("UserId", "RoleId")
select u."Id", r."Id"
from "AspNetUsers" u cross join "AspNetRoles" r
where u."NormalizedEmail" = upper(:'email') and r."NormalizedName" = 'ADMIN'
on conflict do nothing;
commit;
SQL
else
  run_psql -v email="$EMAIL" <<'SQL'
delete from "AspNetUserRoles" ur
using "AspNetUsers" u, "AspNetRoles" r
where ur."UserId" = u."Id" and ur."RoleId" = r."Id"
  and u."NormalizedEmail" = upper(:'email') and r."NormalizedName" = 'ADMIN';
SQL
fi

HOLDS=$(run_psql -v email="$EMAIL" <<'SQL'
select u."Id" || ' | role held: ' || exists (
  select 1 from "AspNetUserRoles" ur join "AspNetRoles" r on r."Id" = ur."RoleId"
  where ur."UserId" = u."Id" and r."NormalizedName" = 'ADMIN')
  || ' | email confirmed: ' || u."EmailConfirmed"
from "AspNetUsers" u where u."NormalizedEmail" = upper(:'email');
SQL
)
echo "account $HOLDS"
case "$MODE:$HOLDS" in
  grant:*"role held: true"*)
    echo "granted. The account is an admin only while its email is also on Admin:Emails"
    echo "(locally: Admin__Emails__0=<email> on the AppHost) and its email is confirmed." ;;
  revoke:*"role held: false"*) echo "revoked." ;;
  *) echo "FAIL: the role row is not in the expected state"; exit 1 ;;
esac
