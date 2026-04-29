#!/usr/bin/env bash
# pre-deploy-check.sh — preprovision hook for azure.yaml
# Verifies production DB resources exist and are correctly configured
# before any deploy tool modifies infrastructure.
#
# ARCHITECTURE: Azure PostgreSQL Flexible Server + Container Apps Environment
# Exit 1 = HARD STOP. Do not proceed with deploy.

set -euo pipefail

# Emergency bypass for exceptional circumstances
if [[ "${SKIP_PREDEPLOY_CHECK:-}" == "1" ]]; then
  echo "⚠️  Pre-deploy checks SKIPPED (SKIP_PREDEPLOY_CHECK=1)"
  exit 0
fi

RG="rg-sstudio-prod"
DB_SERVER="db-3ovvqiybthkb6"
ACA_ENV="cae-3ovvqiybthkb6"
EXPECTED_LOCKS=("do-not-delete-db" "do-not-delete-db-storage")

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

ERRORS=0

echo "=============================================="
echo " PRE-DEPLOY SAFETY CHECK"
echo " Architecture: PostgreSQL Flexible Server"
echo "=============================================="
echo ""

# 1. Check resource locks exist
echo "--- Checking resource locks ---"
LOCK_NAMES=$(az lock list --resource-group "$RG" --query "[].name" -o tsv 2>/dev/null || echo "")
if [ -z "$LOCK_NAMES" ]; then
    LOCK_COUNT=0
else
    LOCK_COUNT=$(printf '%s\n' "$LOCK_NAMES" | grep -c .)
fi

if [ "$LOCK_COUNT" -lt 2 ]; then
    echo -e "${RED}FAIL: Expected at least 2 resource locks, found $LOCK_COUNT${NC}"
    echo "  Run the lock commands from docs/deploy-runbook.md before deploying."
    ERRORS=$((ERRORS + 1))
else
    # Verify expected lock names
    MISSING_LOCKS=()
    for EXPECTED_LOCK in "${EXPECTED_LOCKS[@]}"; do
        if ! echo "$LOCK_NAMES" | grep -q "$EXPECTED_LOCK"; then
            MISSING_LOCKS+=("$EXPECTED_LOCK")
        fi
    done
    
    if [ ${#MISSING_LOCKS[@]} -gt 0 ]; then
        echo -e "${RED}FAIL: Found $LOCK_COUNT locks but missing expected locks: ${MISSING_LOCKS[*]}${NC}"
        echo "  Run the lock commands from docs/deploy-runbook.md before deploying."
        ERRORS=$((ERRORS + 1))
    else
        echo -e "${GREEN}PASS: All expected resource locks present${NC}"
    fi
fi

# 2. Check PostgreSQL Flexible Server exists and is Ready
echo ""
echo "--- Checking PostgreSQL Flexible Server ---"
DB_STATE=$(az postgres flexible-server show \
    --resource-group "$RG" \
    --name "$DB_SERVER" \
    --query "state" -o tsv 2>/dev/null || echo "")

if [ -z "$DB_STATE" ]; then
    echo -e "${RED}FAIL: Flexible Server '$DB_SERVER' not found in $RG${NC}"
    echo "  The database server does not exist. Do NOT proceed."
    ERRORS=$((ERRORS + 1))
elif [ "$DB_STATE" != "Ready" ]; then
    echo -e "${RED}FAIL: Flexible Server state is '$DB_STATE', expected 'Ready'${NC}"
    echo "  The database is not ready. Do NOT proceed."
    ERRORS=$((ERRORS + 1))
else
    echo -e "${GREEN}PASS: Flexible Server '$DB_SERVER' is Ready${NC}"
fi

# 3. Check Container Apps Environment is provisioned
echo ""
echo "--- Checking Container Apps Environment ---"
ACA_STATE=$(az containerapp env show \
    --resource-group "$RG" \
    --name "$ACA_ENV" \
    --query "properties.provisioningState" -o tsv 2>/dev/null || echo "")

if [ -z "$ACA_STATE" ]; then
    echo -e "${RED}FAIL: Container Apps Environment '$ACA_ENV' not found in $RG${NC}"
    ERRORS=$((ERRORS + 1))
elif [ "$ACA_STATE" != "Succeeded" ]; then
    echo -e "${RED}FAIL: Environment provisioning state is '$ACA_STATE', expected 'Succeeded'${NC}"
    ERRORS=$((ERRORS + 1))
else
    echo -e "${GREEN}PASS: Container Apps Environment is provisioned${NC}"
fi

# 4. Check recent backup exists (within 48 hours)
echo ""
echo "--- Checking database backup freshness ---"
LATEST_BACKUP=$(az postgres flexible-server backup list \
    --resource-group "$RG" \
    --name "$DB_SERVER" \
    --query "[-1].completedTime" -o tsv 2>/dev/null || echo "")

if [ -z "$LATEST_BACKUP" ]; then
    echo -e "${RED}FAIL: Could not verify backup existence${NC}"
    echo "  No backups found or backup configuration is broken. Do NOT proceed."
    ERRORS=$((ERRORS + 1))
else
    # Convert backup time to epoch seconds (handle both GNU and BSD date)
    if date --version >/dev/null 2>&1; then
        # GNU date (Linux)
        BACKUP_EPOCH=$(date -d "$LATEST_BACKUP" +%s 2>/dev/null || echo "0")
        NOW_EPOCH=$(date +%s)
    else
        # BSD date (macOS)
        BACKUP_EPOCH=$(date -j -f "%Y-%m-%dT%H:%M:%S" "${LATEST_BACKUP:0:19}" +%s 2>/dev/null || echo "0")
        NOW_EPOCH=$(date +%s)
    fi
    
    if [ "$BACKUP_EPOCH" -eq 0 ]; then
        echo -e "${RED}FAIL: Could not parse backup timestamp '$LATEST_BACKUP'${NC}"
        ERRORS=$((ERRORS + 1))
    else
        HOURS_AGO=$(( (NOW_EPOCH - BACKUP_EPOCH) / 3600 ))
        if [ "$HOURS_AGO" -gt 48 ]; then
            echo -e "${RED}FAIL: Latest backup is $HOURS_AGO hours old (>48h threshold)${NC}"
            echo "  Backup timestamp: $LATEST_BACKUP"
            ERRORS=$((ERRORS + 1))
        else
            echo -e "${GREEN}PASS: Latest backup is $HOURS_AGO hours old${NC}"
        fi
    fi
fi

# Summary
echo ""
echo "=============================================="
if [ "$ERRORS" -gt 0 ]; then
    echo -e "${RED}  $ERRORS CHECK(S) FAILED — DEPLOY BLOCKED${NC}"
    echo ""
    echo "  Fix the issues above before deploying."
    echo "  See docs/deploy-runbook.md for remediation steps."
    echo "=============================================="
    exit 1
else
    echo -e "${GREEN}  ALL CHECKS PASSED — Safe to proceed${NC}"
    echo "=============================================="
    exit 0
fi
