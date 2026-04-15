#!/usr/bin/env bash
# pre-deploy-check.sh — preprovision hook for azure.yaml
# Verifies production DB resources exist and are correctly configured
# before any deploy tool modifies infrastructure.
#
# Exit 1 = HARD STOP. Do not proceed with deploy.

set -euo pipefail

RG="rg-sstudio-prod"
DB_APP="db"
STORAGE_ACCOUNT="vol3ovvqiybthkb6"
FILE_SHARE="db-sentencestudioapphost8351ffded3dbdata"
STORAGE_MOUNT="db-sentencestudioapphost8351ffde"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

ERRORS=0

echo "=============================================="
echo " PRE-DEPLOY SAFETY CHECK"
echo "=============================================="
echo ""

# 1. Check resource locks exist
echo "--- Checking resource locks ---"
LOCK_COUNT=$(az lock list --resource-group "$RG" --query "length(@)" -o tsv 2>/dev/null || echo "0")
if [ "$LOCK_COUNT" -lt 2 ]; then
    echo -e "${RED}FAIL: Expected at least 2 resource locks, found $LOCK_COUNT${NC}"
    echo "  Run the lock commands from docs/deploy-runbook.md before deploying."
    ERRORS=$((ERRORS + 1))
else
    echo -e "${GREEN}PASS: $LOCK_COUNT resource lock(s) found${NC}"
fi

# 2. Check db container app exists
echo ""
echo "--- Checking db container app ---"
DB_EXISTS=$(az containerapp show --name "$DB_APP" --resource-group "$RG" --query "name" -o tsv 2>/dev/null || echo "")
if [ -z "$DB_EXISTS" ]; then
    echo -e "${RED}FAIL: Container app '$DB_APP' not found in $RG${NC}"
    echo "  The database container does not exist. Do NOT proceed."
    ERRORS=$((ERRORS + 1))
else
    echo -e "${GREEN}PASS: Container app '$DB_APP' exists${NC}"
fi

# 3. Check current revision has volume mount
echo ""
echo "--- Checking volume mount on current revision ---"
VOLUMES=$(az containerapp revision list \
    --name "$DB_APP" --resource-group "$RG" \
    --query "[0].properties.template.volumes" -o json 2>/dev/null || echo "null")

if [ "$VOLUMES" = "null" ] || [ "$VOLUMES" = "[]" ]; then
    echo -e "${RED}FAIL: No volume mounts on current db revision${NC}"
    echo "  The database container will lose data on restart. STOP."
    ERRORS=$((ERRORS + 1))
else
    HAS_AZURE_FILE=$(echo "$VOLUMES" | grep -c "AzureFile" || true)
    if [ "$HAS_AZURE_FILE" -eq 0 ]; then
        echo -e "${RED}FAIL: Volume mount exists but is not AzureFile type${NC}"
        ERRORS=$((ERRORS + 1))
    else
        echo -e "${GREEN}PASS: AzureFile volume mount present${NC}"
    fi
fi

# 4. Check storage account exists
echo ""
echo "--- Checking storage account ---"
SA_EXISTS=$(az storage account show --name "$STORAGE_ACCOUNT" --resource-group "$RG" --query "name" -o tsv 2>/dev/null || echo "")
if [ -z "$SA_EXISTS" ]; then
    echo -e "${RED}FAIL: Storage account '$STORAGE_ACCOUNT' not found${NC}"
    ERRORS=$((ERRORS + 1))
else
    echo -e "${GREEN}PASS: Storage account '$STORAGE_ACCOUNT' exists${NC}"
fi

# 5. Check file share exists and is non-empty
echo ""
echo "--- Checking file share ---"
FILE_COUNT=$(az storage file list \
    --account-name "$STORAGE_ACCOUNT" \
    --share-name "$FILE_SHARE" \
    --query "length(@)" -o tsv 2>/dev/null || echo "0")

if [ "$FILE_COUNT" -eq 0 ]; then
    echo -e "${RED}FAIL: File share '$FILE_SHARE' is empty or does not exist${NC}"
    echo "  The database storage has no files. This is extremely dangerous."
    ERRORS=$((ERRORS + 1))
else
    echo -e "${GREEN}PASS: File share contains $FILE_COUNT item(s)${NC}"
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
