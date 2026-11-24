#!/bin/bash
# The ailoha command as a fix session of the feedback check gets it (published as
# /Users/Shared/sstudio-feedback/ailoha/bin/ailoha by scripts/admin-feedback-check.sh). It
# drives only an app of this macOS user's own: other apps on this Mac have Ailoha agents on the
# neighbouring ports, they are the Captain's, and plain ailoha would find one of them by itself.
#
# It runs the real command only when AILOHA_AGENT_PORT names a port that a process of this
# user listens on (scripts/session-sim.sh prints it), and takes no other way of choosing an
# agent: no port or host option, no broker.
set -u
real="$(cd "$(dirname "$0")/../lib" 2>/dev/null && pwd)/ailoha"
[ -x "$real" ] || { echo "STOP: the ailoha command is not beside this script (../lib/ailoha)." >&2; exit 2; }
for arg in "$@"; do
  case "$arg" in
    -ap|--agent-port|--agent-port=*|-ah|--agent-host|--agent-host=*|broker)
      echo "STOP: give the agent's port as AILOHA_AGENT_PORT=<port>, the one scripts/session-sim.sh printed; '$arg' is not taken here." >&2
      exit 2 ;;
  esac
done
case "${AILOHA_AGENT_PORT:-}" in
  ""|*[!0-9]*)
    echo "STOP: set AILOHA_AGENT_PORT to the port scripts/session-sim.sh printed. Without it ailoha would find another app's agent on this Mac." >&2
    exit 2 ;;
esac
unset AILOHA_AGENT_HOST
# lsof shows a user only its own processes' sockets, so a row means the port is this user's.
listening="$(lsof -nP -a -u "$(id -u)" -iTCP:"$AILOHA_AGENT_PORT" -sTCP:LISTEN 2>/dev/null | awk 'NR > 1')"
[ -n "$listening" ] || {
  echo "STOP: no app of yours listens on port $AILOHA_AGENT_PORT: it is another user's app, or yours has started again on another port (scripts/session-sim.sh port)." >&2
  exit 2
}
exec "$real" "$@"
