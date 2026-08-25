#!/usr/bin/env bash
#
# clone-local-db-volume.sh
#
# Clones a local Docker volume that holds a PostgreSQL data directory into a NEW volume, so a
# worktree can run `aspire run` against a copy of an established local database instead of an
# empty one.
#
# Safety rules this script will not break:
#   * The source volume is only ever mounted read-only, or read through a running server.
#   * The destination must not already exist. An existing destination is refused, never reused,
#     never overwritten, never removed.
#   * Nothing is dropped, truncated, deleted, or reset in either volume.
#   * On failure the partially written destination is LEFT IN PLACE and the exact
#     `docker volume rm` command is printed, so removal is always the operator's decision.
#   * A file-level copy runs only when no running container has the source volume mounted.
#     Copying a live PostgreSQL data directory can capture a torn page; use --mode dump instead.
#
# Usage:
#   scripts/clone-local-db-volume.sh --source <volume> --destination <volume> [options]
#
# Options:
#   --source        <name>  Existing volume holding the PostgreSQL data directory. Required.
#   --destination   <name>  New volume to create. Must not exist. Required.
#   --mode  auto|copy|dump  auto (default) picks dump when the source is in use by a running
#                           container, and copy when it is not.
#   --image         <ref>   PostgreSQL image used for the helper containers. Default postgres:17.
#                           Match the tag the AppHost pins, so the on-disk layout matches.
#   --database      <name>  Database name, used by --mode dump and by --verify. Default postgres.
#   --username      <name>  Superuser name, used by --mode dump and by --verify. Default postgres.
#   --verify                After cloning, start a throwaway server on the destination, wait for
#                           readiness, optionally count --verify-table, then stop and remove that
#                           throwaway container. The destination volume is kept.
#   --verify-table  <name>  Table to count during --verify. Quoted identifiers are supported.
#   --dry-run               Print the plan and exit without creating anything.
#   -h | --help             Show this help.
#
# Credentials:
#   --mode dump and --verify authenticate with $PGPASSWORD when it is set. Nothing is read from,
#   or written to, the repository. Never pass a password on the command line.
#
set -euo pipefail

SOURCE_VOLUME=""
DESTINATION_VOLUME=""
MODE="auto"
IMAGE="postgres:17"
DATABASE="postgres"
USERNAME="postgres"
VERIFY="false"
VERIFY_TABLE=""
DRY_RUN="false"

die() {
    echo "ERROR: $*" >&2
    exit 1
}

usage() {
    sed -n '3,45p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --source) SOURCE_VOLUME="${2:-}"; shift 2 ;;
        --destination) DESTINATION_VOLUME="${2:-}"; shift 2 ;;
        --mode) MODE="${2:-}"; shift 2 ;;
        --image) IMAGE="${2:-}"; shift 2 ;;
        --database) DATABASE="${2:-}"; shift 2 ;;
        --username) USERNAME="${2:-}"; shift 2 ;;
        --verify) VERIFY="true"; shift ;;
        --verify-table) VERIFY_TABLE="${2:-}"; shift 2 ;;
        --dry-run) DRY_RUN="true"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "Unknown argument: $1" ;;
    esac
done

[[ -n "$SOURCE_VOLUME" ]] || die "--source is required."
[[ -n "$DESTINATION_VOLUME" ]] || die "--destination is required."
[[ "$SOURCE_VOLUME" != "$DESTINATION_VOLUME" ]] || die "--source and --destination must differ."
[[ "$MODE" =~ ^(auto|copy|dump)$ ]] || die "--mode must be auto, copy, or dump."

command -v docker >/dev/null 2>&1 || die "docker is not on PATH."

docker volume inspect "$SOURCE_VOLUME" >/dev/null 2>&1 \
    || die "Source volume '$SOURCE_VOLUME' does not exist."

# Refuse an existing destination. This is the guard that makes the script safe to re-run: it can
# never silently write into, merge with, or replace a volume somebody else is relying on.
if docker volume inspect "$DESTINATION_VOLUME" >/dev/null 2>&1; then
    die "Destination volume '$DESTINATION_VOLUME' already exists. Refusing to write into it.
     Choose a new name, or remove that volume yourself if you are certain it is disposable:
       docker volume rm $DESTINATION_VOLUME"
fi

# Any container currently running with the source mounted means a live PostgreSQL may be writing.
RUNNING_USERS="$(docker ps --format '{{.Names}}' --filter "volume=$SOURCE_VOLUME" | tr '\n' ' ' | sed 's/ *$//')"

if [[ "$MODE" == "auto" ]]; then
    if [[ -n "$RUNNING_USERS" ]]; then MODE="dump"; else MODE="copy"; fi
fi

if [[ "$MODE" == "copy" && -n "$RUNNING_USERS" ]]; then
    die "Source volume '$SOURCE_VOLUME' is mounted by running container(s): $RUNNING_USERS
     A file-level copy of a live PostgreSQL data directory can capture a torn page.
     Stop that container first, or re-run with --mode dump for a consistent logical copy."
fi

if [[ "$MODE" == "dump" && -z "$RUNNING_USERS" ]]; then
    die "--mode dump needs a running PostgreSQL container on '$SOURCE_VOLUME', and none is running.
     Start the source stack, or re-run with --mode copy now that nothing is writing to it."
fi

echo "Source volume ......... $SOURCE_VOLUME"
echo "Destination volume .... $DESTINATION_VOLUME (will be created)"
echo "Mode .................. $MODE"
echo "Helper image .......... $IMAGE"
[[ "$MODE" == "dump" || "$VERIFY" == "true" ]] && echo "Database / user ....... $DATABASE / $USERNAME"
[[ -n "$RUNNING_USERS" ]] && echo "Running on source ..... $RUNNING_USERS" || echo "Running on source ..... none (source is stopped)"

if [[ "$DRY_RUN" == "true" ]]; then
    echo
    echo "Dry run: nothing was created."
    exit 0
fi

CREATED_DESTINATION="false"
VERIFY_CONTAINER="${DESTINATION_VOLUME}-verify"
DUMP_CONTAINER="${DESTINATION_VOLUME}-restore"

on_failure() {
    local status=$?
    [[ $status -eq 0 ]] && return 0
    echo >&2
    echo "Clone FAILED (exit $status)." >&2
    echo "The source volume '$SOURCE_VOLUME' was not modified." >&2
    if [[ "$CREATED_DESTINATION" == "true" ]]; then
        echo "The destination volume '$DESTINATION_VOLUME' was created and is being left in place on purpose." >&2
        echo "Inspect it, or remove it yourself when you are sure:" >&2
        echo "  docker volume rm $DESTINATION_VOLUME" >&2
    fi
    return $status
}
trap on_failure EXIT

echo
echo "Creating destination volume..."
docker volume create "$DESTINATION_VOLUME" >/dev/null
CREATED_DESTINATION="true"

if [[ "$MODE" == "copy" ]]; then
    echo "Copying data directory (source mounted read-only)..."
    docker run --rm \
        -v "$SOURCE_VOLUME":/src:ro \
        -v "$DESTINATION_VOLUME":/dst \
        "$IMAGE" \
        bash -c 'cp -a /src/. /dst/'

    # A stale lock file from a source container that was killed rather than shut down would stop
    # the clone from starting. Removing it touches the new copy only.
    docker run --rm -v "$DESTINATION_VOLUME":/dst "$IMAGE" \
        bash -c 'rm -f /dst/postmaster.pid /dst/postgresql.auto.conf.tmp' >/dev/null
else
    SOURCE_CONTAINER="${RUNNING_USERS%% *}"
    echo "Dumping from running container '$SOURCE_CONTAINER' (read-only operation)..."
    DUMP_FILE="$(mktemp -t coach-db-clone)"
    # shellcheck disable=SC2064
    trap "rm -f '$DUMP_FILE'" RETURN
    docker exec -e PGPASSWORD="${PGPASSWORD:-}" "$SOURCE_CONTAINER" \
        pg_dumpall -U "$USERNAME" --clean --if-exists > "$DUMP_FILE"

    echo "Initializing destination server and restoring..."
    docker run -d --name "$DUMP_CONTAINER" \
        -e POSTGRES_USER="$USERNAME" \
        -e POSTGRES_PASSWORD="${PGPASSWORD:-postgres}" \
        -e POSTGRES_DB="$DATABASE" \
        -v "$DESTINATION_VOLUME":/var/lib/postgresql/data \
        "$IMAGE" >/dev/null

    for _ in $(seq 1 60); do
        if docker exec "$DUMP_CONTAINER" pg_isready -U "$USERNAME" >/dev/null 2>&1; then break; fi
        sleep 1
    done

    docker exec -i -e PGPASSWORD="${PGPASSWORD:-postgres}" "$DUMP_CONTAINER" \
        psql -U "$USERNAME" -d postgres < "$DUMP_FILE" >/dev/null

    docker stop "$DUMP_CONTAINER" >/dev/null
    docker rm "$DUMP_CONTAINER" >/dev/null
    rm -f "$DUMP_FILE"
fi

echo "Clone written to '$DESTINATION_VOLUME'."

if [[ "$VERIFY" == "true" ]]; then
    echo
    echo "Verifying the clone in a throwaway container..."
    docker run -d --name "$VERIFY_CONTAINER" \
        -e POSTGRES_PASSWORD="${PGPASSWORD:-postgres}" \
        -v "$DESTINATION_VOLUME":/var/lib/postgresql/data \
        "$IMAGE" >/dev/null

    READY="false"
    for _ in $(seq 1 60); do
        if docker exec "$VERIFY_CONTAINER" pg_isready -U "$USERNAME" -d "$DATABASE" >/dev/null 2>&1; then
            READY="true"
            break
        fi
        sleep 1
    done

    if [[ "$READY" != "true" ]]; then
        docker logs --tail 30 "$VERIFY_CONTAINER" >&2 || true
        docker rm -f "$VERIFY_CONTAINER" >/dev/null 2>&1 || true
        die "The cloned server did not become ready. The destination volume was kept for inspection."
    fi

    echo "  PostgreSQL accepted connections on the clone."

    if [[ -n "$VERIFY_TABLE" ]]; then
        COUNT="$(docker exec -e PGPASSWORD="${PGPASSWORD:-}" "$VERIFY_CONTAINER" \
            psql -U "$USERNAME" -d "$DATABASE" -tA -c "select count(*) from $VERIFY_TABLE;")"
        echo "  $VERIFY_TABLE row count: $COUNT"
    fi

    # Only the throwaway verification container is removed. The clone volume stays.
    docker stop "$VERIFY_CONTAINER" >/dev/null
    docker rm "$VERIFY_CONTAINER" >/dev/null
    echo "  Verification container stopped and removed."
fi

trap - EXIT

cat <<EOF

Done. Source '$SOURCE_VOLUME' was not modified.

Run the stack against the clone:

  LocalDb__DataVolume=$DESTINATION_VOLUME aspire run

EOF
