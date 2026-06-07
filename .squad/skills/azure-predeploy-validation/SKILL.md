# Azure Pre-Deploy Validation Scripts

**Domain:** Azure Infrastructure, DevOps, Database Safety  
**Pattern:** Preprovision hook scripts that block deploy on infrastructure validation failures  
**Verified Against:** Azure PostgreSQL Flexible Server, Container Apps Environment

## When to Use This Skill

- Writing `azure.yaml` preprovision hooks that validate production resource state
- Protecting critical stateful resources (databases, storage) from deploy tool side effects
- Implementing "read before modify" safety gates for infrastructure-as-code workflows
- Creating automated checks for backup freshness, resource locks, or provisioning state

## Core Principles

### 1. Read-Only Verification
Pre-deploy checks should **NEVER modify resources**. They verify state only.

```bash
# ✅ CORRECT: Query state
DB_STATE=$(az postgres flexible-server show --name "$SERVER" --query "state" -o tsv)

# ❌ WRONG: Modify during check
az postgres flexible-server restart --name "$SERVER"  # NO!
```

### 2. Hard Stop Exit Semantics
Exit code 1 = DEPLOY BLOCKED. Exit code 0 = Safe to proceed. No warnings-as-pass.

```bash
if [ "$ERRORS" -gt 0 ]; then
    echo "DEPLOY BLOCKED"
    exit 1  # Hard stop
else
    echo "Safe to proceed"
    exit 0
fi
```

### 3. Emergency Bypass
Always provide a `SKIP_*_CHECK=1` environment variable for exceptional circumstances.

```bash
if [[ "${SKIP_PREDEPLOY_CHECK:-}" == "1" ]]; then
  echo "⚠️  Pre-deploy checks SKIPPED"
  exit 0
fi
```

### 4. Actionable Failure Messages
Every FAIL must tell the operator what's wrong and how to fix it.

```bash
if [ -z "$DB_EXISTS" ]; then
    echo "FAIL: Database server not found"
    echo "  Remediation: Check resource group name in azure.yaml"
    echo "  See docs/deploy-runbook.md for setup steps"
    ERRORS=$((ERRORS + 1))
fi
```

## Validation Patterns

### PostgreSQL Flexible Server State

**Check:** Server exists and is `Ready`

```bash
DB_STATE=$(az postgres flexible-server show \
    --resource-group "$RG" \
    --name "$DB_SERVER" \
    --query "state" -o tsv 2>/dev/null || echo "")

if [ -z "$DB_STATE" ]; then
    echo "FAIL: Flexible Server not found"
    ERRORS=$((ERRORS + 1))
elif [ "$DB_STATE" != "Ready" ]; then
    echo "FAIL: Server state is '$DB_STATE', expected 'Ready'"
    ERRORS=$((ERRORS + 1))
fi
```

**Why:** Prevents deploying app containers when the database is offline, restarting, or disabled.

### Resource Lock Verification

**Check:** Expected locks exist at RG scope

```bash
LOCK_NAMES=$(az lock list --resource-group "$RG" --query "[].name" -o tsv)
EXPECTED_LOCKS=("do-not-delete-db" "do-not-delete-db-storage")

for EXPECTED_LOCK in "${EXPECTED_LOCKS[@]}"; do
    if ! echo "$LOCK_NAMES" | grep -q "$EXPECTED_LOCK"; then
        echo "FAIL: Missing lock '$EXPECTED_LOCK'"
        ERRORS=$((ERRORS + 1))
    fi
done
```

**Why:** Confirms CanNotDelete locks are in place before deploy tools run. Catches drift from manual lock removal.

### Backup Freshness (48h threshold)

**Check:** Latest automated backup completed within 48 hours

```bash
LATEST_BACKUP=$(az postgres flexible-server backup list \
    --resource-group "$RG" \
    --name "$DB_SERVER" \
    --query "[-1].completedTime" -o tsv)

# Handle BSD date (macOS) vs GNU date (Linux)
if date --version >/dev/null 2>&1; then
    # GNU date
    BACKUP_EPOCH=$(date -d "$LATEST_BACKUP" +%s)
else
    # BSD date
    BACKUP_EPOCH=$(date -j -f "%Y-%m-%dT%H:%M:%S" "${LATEST_BACKUP:0:19}" +%s)
fi

HOURS_AGO=$(( ($(date +%s) - BACKUP_EPOCH) / 3600 ))
if [ "$HOURS_AGO" -gt 48 ]; then
    echo "FAIL: Latest backup is $HOURS_AGO hours old (>48h threshold)"
    ERRORS=$((ERRORS + 1))
fi
```

**Why:** Early detection of backup configuration drift. Automated backups failing silently is a disaster waiting to happen.

**Gotcha:** PostgreSQL Flexible Server backup list CLI emits deprecation warnings (May 2026) about `--name` argument repurposing. Current command still works but will need `--server-name` in future.

### Container Apps Environment State

**Check:** Environment provisioning state is `Succeeded`

```bash
ACA_STATE=$(az containerapp env show \
    --resource-group "$RG" \
    --name "$ACA_ENV" \
    --query "properties.provisioningState" -o tsv)

if [ "$ACA_STATE" != "Succeeded" ]; then
    echo "FAIL: Environment state is '$ACA_STATE', expected 'Succeeded'"
    ERRORS=$((ERRORS + 1))
fi
```

**Why:** Ensures ACA environment is healthy before deploy modifies container apps. Catches transient provisioning failures.

## Script Structure Template

```bash
#!/usr/bin/env bash
set -euo pipefail  # Fail fast

# Emergency bypass
if [[ "${SKIP_PREDEPLOY_CHECK:-}" == "1" ]]; then
  echo "⚠️  Checks SKIPPED"
  exit 0
fi

# Resource names (hard-coded or from config)
RG="rg-prod"
DB_SERVER="db-xyz"

RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

ERRORS=0

echo "=============================================="
echo " PRE-DEPLOY SAFETY CHECK"
echo "=============================================="

# CHECK 1: Resource exists
echo "--- Checking resource ---"
RESOURCE_EXISTS=$(az ... --query "name" -o tsv 2>/dev/null || echo "")
if [ -z "$RESOURCE_EXISTS" ]; then
    echo -e "${RED}FAIL: Resource not found${NC}"
    ERRORS=$((ERRORS + 1))
else
    echo -e "${GREEN}PASS: Resource exists${NC}"
fi

# CHECK 2: State is valid
# ... (similar pattern)

# Summary
echo ""
echo "=============================================="
if [ "$ERRORS" -gt 0 ]; then
    echo -e "${RED}  $ERRORS CHECK(S) FAILED — DEPLOY BLOCKED${NC}"
    echo "  See docs/deploy-runbook.md for remediation"
    exit 1
else
    echo -e "${GREEN}  ALL CHECKS PASSED — Safe to proceed${NC}"
    exit 0
fi
```

## Integration with azure.yaml

```yaml
# azure.yaml
hooks:
  preprovision:
    posix:
      shell: sh
      run: ./scripts/pre-deploy-check.sh
      continueOnError: false  # Hard stop on failure
      interactive: false
```

**Effect:** `azd deploy` runs the script before any infrastructure modifications. Non-zero exit aborts the entire deploy.

## Common Pitfalls

### ❌ Modify-then-check
Don't create resources in the check script "if missing". That's provisioning logic, not validation.

### ❌ Warning-as-pass
Don't exit 0 on warnings. If backup is stale, that's a FAIL. Deploy should block.

```bash
# ❌ WRONG
if [ "$HOURS_AGO" -gt 48 ]; then
    echo "WARNING: Backup is stale"  # Still exits 0 later!
fi

# ✅ CORRECT
if [ "$HOURS_AGO" -gt 48 ]; then
    ERRORS=$((ERRORS + 1))  # Increment error count
fi
```

### ❌ Assume GNU date everywhere
macOS uses BSD date. Always check `date --version` and branch.

### ❌ Hide stdout/stderr
Don't silence `2>/dev/null` unless you handle the empty return. Use `|| echo ""` fallback.

## Testing Strategy

1. **Happy path:** Run against healthy production. Verify exit 0.
2. **Forced failure:** Temporarily rename resource. Verify exit 1 with actionable message.
3. **Emergency bypass:** Run with `SKIP_PREDEPLOY_CHECK=1`. Verify exit 0 immediately.
4. **CI dry-run:** Run in CI before actual deploy to catch environment-specific issues (GNU date, missing az CLI extensions).

## Real-World Example

`scripts/pre-deploy-check.sh` in this repository validates:
- PostgreSQL Flexible Server state (Ready)
- RG-scoped locks (do-not-delete-db, do-not-delete-db-storage)
- Container Apps Environment provisioning (Succeeded)
- Backup freshness (<48h)

Test run output:
```
==============================================
 PRE-DEPLOY SAFETY CHECK
 Architecture: PostgreSQL Flexible Server
==============================================

--- Checking resource locks ---
PASS: All expected resource locks present

--- Checking PostgreSQL Flexible Server ---
PASS: Flexible Server 'db-rsn72awybem6s' is Ready

--- Checking Container Apps Environment ---
PASS: Container Apps Environment is provisioned

--- Checking database backup freshness ---
PASS: Latest backup is 5 hours old

==============================================
  ALL CHECKS PASSED — Safe to proceed
==============================================
```

Exit code: 0

## References

- **Decision:** `.squad/decisions/inbox/wash-predeploy-flexserver.md` (Flexible Server validation implementation)
- **Script:** `scripts/pre-deploy-check.sh` (production implementation)
- **Runbook:** `docs/deploy-runbook.md` (operator-facing remediation steps)

## Related Patterns

- **SQLite migration history reconcile:** Pre-commit validation that checks for migration drift
- **Dotnet SDK detection:** Multi-layer SDK resolution validation before build commands
- **Structured import results:** Per-item validation with detailed failure reasons
