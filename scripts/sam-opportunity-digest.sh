#!/usr/bin/env bash
# sam-opportunity-digest.sh — the production reviewer path for the Sam opportunity ledger.
#
# Prints (or writes) a content-free digest of the Sam opportunity ledger and the learner reports
# that raised rows in it: counts, closed-vocabulary codes, review statuses, timestamps, distinct
# learner counts, and content-free fingerprints. It carries NO learner content, owner ids,
# conversation ids, message ids, tool arguments, emails, or decrypted evidence.
#
# The operator review surface (/operator/sam-opportunities) can decrypt learner messages and stays
# Development-only until this codebase has an admin authorization primitive. This script is what
# makes "Reported for review" true in Production without shipping that surface.
#
# Usage:
#   ./scripts/sam-opportunity-digest.sh                       # last 7 days, markdown, to stdout
#   ./scripts/sam-opportunity-digest.sh --days 30             # a wider window
#   ./scripts/sam-opportunity-digest.sh --days 0              # everything still retained
#   ./scripts/sam-opportunity-digest.sh --json                # JSON instead of markdown
#   ./scripts/sam-opportunity-digest.sh --output digest.md    # write to a file
#
# Credentials — pick ONE. Nothing is stored in this repository and nothing is echoed.
#
#   1) A connection string already in the environment:
#        export COACH_DIGEST_CONNECTION_STRING='Host=...;Database=...;Username=...;Password=...'
#
#   2) A connection string in Key Vault (names supplied by you — this script invents none):
#        export COACH_DIGEST_KEYVAULT=dbkv-rsn72awybem6s
#        export COACH_DIGEST_KEYVAULT_SECRET=<secret-name>
#      Discover the name with:
#        az keyvault secret list --vault-name "$COACH_DIGEST_KEYVAULT" -o table
#
#   3) An Entra token, no password anywhere:
#        export COACH_DIGEST_AZURE_IDENTITY=1
#        export COACH_DIGEST_HOST=db-rsn72awybem6s.postgres.database.azure.com
#        export COACH_DIGEST_DATABASE=sentencestudio
#        export COACH_DIGEST_USER=<your-entra-principal>
#
# Prerequisites and the weekly review process: docs/sam-opportunity-digest.md
#
# Exit codes: 0 ok · 1 usage · 2 not configured · 3 read failed

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/tools/SamOpportunityDigest/SamOpportunityDigest.csproj"

if [[ ! -f "$PROJECT" ]]; then
  echo "Could not find $PROJECT. Run this from a checkout of the repository." >&2
  exit 1
fi

# `--help` has to work before the credential check, or the only way to read the usage is to
# already have configured the thing the usage explains.
for arg in "$@"; do
  if [[ "$arg" == "--help" || "$arg" == "-h" ]]; then
    SKIP_CREDENTIAL_CHECK=1
    break
  fi
done

# ── Credential resolution ────────────────────────────────────────────────────
# Resolved into the environment of the child process only. Never printed, never written to disk,
# and never passed on the command line (where it would land in the process table and in shell
# history).

if [[ "${SKIP_CREDENTIAL_CHECK:-0}" != "1" \
      && -z "${COACH_DIGEST_CONNECTION_STRING:-}" \
      && -n "${COACH_DIGEST_KEYVAULT:-}" \
      && -n "${COACH_DIGEST_KEYVAULT_SECRET:-}" ]]; then

  if ! command -v az >/dev/null 2>&1; then
    echo "COACH_DIGEST_KEYVAULT is set but the Azure CLI is not installed." >&2
    exit 2
  fi

  echo "Reading the connection string from Key Vault ${COACH_DIGEST_KEYVAULT}..." >&2

  if ! COACH_DIGEST_CONNECTION_STRING="$(az keyvault secret show \
        --vault-name "$COACH_DIGEST_KEYVAULT" \
        --name "$COACH_DIGEST_KEYVAULT_SECRET" \
        --query value -o tsv 2>/dev/null)"; then
    echo "Could not read secret '${COACH_DIGEST_KEYVAULT_SECRET}' from vault '${COACH_DIGEST_KEYVAULT}'." >&2
    echo "List the available names with: az keyvault secret list --vault-name ${COACH_DIGEST_KEYVAULT} -o table" >&2
    exit 2
  fi

  export COACH_DIGEST_CONNECTION_STRING
fi

if [[ "${SKIP_CREDENTIAL_CHECK:-0}" != "1" \
      && -z "${COACH_DIGEST_CONNECTION_STRING:-}" \
      && "${COACH_DIGEST_AZURE_IDENTITY:-}" != "1" ]]; then
  cat >&2 <<'MSG'
No database credential is configured, so there is nothing to read and this script will not guess.

Set ONE of:
  COACH_DIGEST_CONNECTION_STRING
  COACH_DIGEST_KEYVAULT + COACH_DIGEST_KEYVAULT_SECRET   (read via the Azure CLI)
  COACH_DIGEST_AZURE_IDENTITY=1 + COACH_DIGEST_HOST + COACH_DIGEST_DATABASE + COACH_DIGEST_USER

See docs/sam-opportunity-digest.md.
MSG
  exit 2
fi

# ── Run ──────────────────────────────────────────────────────────────────────
# The tool applies `default_transaction_read_only=on` as a server-side startup option, so the
# session cannot write even if the projection were changed to try.
#
# Built first, with all build output sent to stderr, then executed directly. `dotnet run` writes
# MSBuild output to stdout, which would make `--json` emit a build log with a JSON document
# somewhere inside it.

BUILD_DIR="$(dirname "$PROJECT")/bin/Release/net10.0"

dotnet build "$PROJECT" --configuration Release --verbosity quiet >&2

if [[ -x "$BUILD_DIR/sam-opportunity-digest" ]]; then
  exec "$BUILD_DIR/sam-opportunity-digest" "$@"
fi

exec dotnet "$BUILD_DIR/sam-opportunity-digest.dll" "$@"
