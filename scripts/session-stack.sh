#!/bin/bash
# A web stack of your own for one check: the API, the web app and the workers with their own
# database, cache and storage, on ports of their own, beside whatever else runs on this Mac and
# never touching it. Made for a fix session of the feedback check (docs/admin-feedback-fix.md),
# which runs as a macOS user with its own Docker; it works for anyone with a Docker that is up.
#
#   scripts/session-stack.sh up [--no-build]   builds the AppHost, starts the stack, waits for
#                                              the API and the web app, prints their addresses
#   scripts/session-stack.sh urls              prints API=<address> and WEBAPP=<address>
#   scripts/session-stack.sh down              stops it and removes its containers and volumes
#
# Everything it needs that is a secret on the Captain's stack is made up here: the database
# login, the token and feedback keys, and placeholders for the ElevenLabs key and the GitHub
# token. So sign-in, plans, activities and sync work; voice does not, and neither does Sam or
# any other AI feature, which needs an Azure sign-in this stack does not have. The local test
# accounts are seeded (docs/local-dev-test-accounts.md); e2e@test.local is its admin.
#
# It never runs `aspire stop --all` and never removes a container or a volume it did not make:
# its two named volumes, and the containers that mount them. The stack's other containers end
# with it and leave volumes without a name. On a Docker of this user's own (Colima under its
# home, as a fix session has) the unused ones made since `up` are removed too; on any other
# Docker they are left, because nothing says whose they are.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APPHOST="$ROOT/src/SentenceStudio.AppHost/SentenceStudio.AppHost.csproj"
# Outside the worktree, so the worktree stays as git has it.
STATE="${TMPDIR:-/tmp}/sstudio-session-stack-$(printf '%s' "$ROOT" | shasum | cut -c1-12)"
WAIT=300

say() { printf '%s\n' "$*" >&2; }
stop() { say "STOP: $*"; exit 1; }

# Ends a command that runs over its time, with what it started. (A watch by the clock: aspire
# and docker do not end on an alarm.) $1 seconds, rest: the command.
within() {
  local limit=$1 waited=0; shift
  "$@" & local running=$!
  while kill -0 "$running" 2>/dev/null; do
    if [ "$waited" -ge "$limit" ]; then kill -TERM "$running" 2>/dev/null; wait "$running" 2>/dev/null; return 124; fi
    sleep 1; waited=$((waited + 1))
  done
  wait "$running"
}

addresses() {
  aspire describe --apphost "$APPHOST" --format Json 2>/dev/null | python3 -c '
import json, sys
try:
    found = json.load(sys.stdin)
except ValueError:
    sys.exit(1)
resources = found.get("resources", []) if isinstance(found, dict) else found
wanted = {"api": "API", "webapp": "WEBAPP"}
said = 0
for resource in resources:
    name = (resource.get("displayName") or resource.get("name") or "").split("-")[0]
    urls = [u.get("url") for u in resource.get("urls") or [] if str(u.get("url", "")).startswith("http://")]
    if name in wanted and urls:
        print(f"{wanted.pop(name)}={urls[0]}")
        said += 1
sys.exit(0 if said == 2 else 1)'
}

up() {
  local build=yes
  [ "${1:-}" = --no-build ] && build=no
  command -v aspire >/dev/null || stop "there is no aspire command on PATH."
  # A fix session's Docker is started as the session starts and takes about half a minute.
  local tries=0
  until within 10 docker info >/dev/null 2>&1; do
    tries=$((tries + 1))
    [ "$tries" -lt 9 ] || stop "Docker is not up (docker info did not answer in 90 s)."
    [ "$tries" -gt 1 ] || say "waiting for Docker"
    sleep 5
  done
  [ -e "$STATE/name" ] && stop "a stack of this worktree is already recorded in $STATE: run $0 down first."
  mkdir -p "$STATE/data" || stop "$STATE could not be made."
  local name="sstack-$(date +%Y%m%d%H%M%S)-$$"
  printf '%s\n' "$name" > "$STATE/name"

  if [ "$build" = yes ]; then
    say "building the AppHost (half a minute, or two to three from nothing)"
    dotnet build "$APPHOST" -p:IncludeMobileTargets=false -p:ValidateXcodeVersion=false -nologo -v:q \
        > "$STATE/build.log" 2>&1 \
      || { grep -E ' error ' "$STATE/build.log" | sort -u | head -10 >&2; stop "the AppHost did not build (log: $STATE/build.log)."; }
  fi

  export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true
  export LocalDb__DataVolume="$name-db" LocalAzurite__DataVolume="$name-storage"
  export WebApp__AppDataRoot="$STATE/data/web" SENTENCESTUDIO_APPDATA_ROOT="$STATE/data/app"
  export Parameters__dbUser=stack Parameters__dbPassword="$(openssl rand -hex 16)"
  export Parameters__jwtkey="$(openssl rand -hex 32)" Parameters__feedbackhmackey="$(openssl rand -hex 32)"
  # Placeholders: nothing real is behind them, so voice and filing to GitHub do not work here.
  export Parameters__elevenlabskey=not-set-on-a-session-stack Parameters__githubpat=not-set-on-a-session-stack
  export Admin__Emails__0=e2e@test.local

  within 20 docker volume ls -q 2>/dev/null | sort > "$STATE/volumes-before"
  say "starting the stack"
  within 120 aspire start --apphost "$APPHOST" --isolated -lp http --no-build --non-interactive --format Json \
      > "$STATE/start.json" 2> "$STATE/start.err" \
    || { tail -5 "$STATE/start.err" >&2; stop "aspire start failed; run $0 down to clear what it left."; }
  local resource
  for resource in api webapp; do
    say "waiting for $resource"
    within "$WAIT" aspire wait "$resource" --apphost "$APPHOST" > "$STATE/wait-$resource.log" 2>&1 \
      || { tail -3 "$STATE/wait-$resource.log" >&2; stop "$resource was not ready in $WAIT s; run $0 down to clear what was started."; }
  done
  addresses || stop "the stack is up but its addresses could not be read (aspire describe)."
}

down() {
  local name="" left
  [ -e "$STATE/name" ] && name="$(cat "$STATE/name")"
  if command -v aspire >/dev/null; then
    within 90 aspire stop --apphost "$APPHOST" --force >/dev/null 2>&1
  fi
  case "$name" in
    sstack-*)
      # Only what mounts this stack's own volumes, then the volumes.
      left="$(within 20 docker ps -aq --filter "volume=$name-db" --filter "volume=$name-storage" 2>/dev/null)"
      [ -z "$left" ] || within 60 docker rm -f $left >/dev/null 2>&1
      within 30 docker volume rm "$name-db" "$name-storage" >/dev/null 2>&1
      case "${DOCKER_HOST:-}" in
        "unix://$HOME/.colima/"*)
          if [ -f "$STATE/volumes-before" ]; then
            # The stack's other containers take a moment to go; their volumes are unused after.
            sleep 5
            left="$(within 20 docker volume ls -q --filter dangling=true 2>/dev/null | sort \
                      | comm -13 "$STATE/volumes-before" - | grep -E '^[0-9a-f]{64}$')"
            [ -z "$left" ] || within 30 docker volume rm $left >/dev/null 2>&1
          fi ;;
      esac ;;
  esac
  rm -rf "$STATE"
  say "stopped"
}

case "${1:-}" in
  up) shift; up "$@" ;;
  urls) addresses || stop "no stack of this worktree is up." ;;
  down) down ;;
  *) sed -n '2,/^set -uo pipefail/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//' >&2; exit 2 ;;
esac
