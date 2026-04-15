# Decision: Azure Resource Locks & Deploy Safety Hardening

**Author:** Wash (Backend Dev)
**Date:** 2025-07-25
**Status:** ENACTED
**Priority:** P0
**Trigger:** Follow-up to production data loss incident (2025-07-25); implementing the 5-layer defense model requested by Captain

---

## What Happened

On 2025-07-25, `aspire deploy` recreated the Postgres container without its Azure File share volume mount, destroying all production user data. Zoe's decision (`zoe-production-data-safety.md`) established governance rules and a migration plan. This decision implements 5 additional Azure-level defense layers to prevent a repeat.

## What Was Implemented

### Layer 1: Backup (already in place)
Mandatory pg_dump or Azure File share download before every deploy. Documented in `docs/deploy-runbook.md` Step 1. This is the last line of defense -- even if everything else fails, a verified backup enables recovery.

### Layer 2: Resource Locks (applied now)
Azure `CanNotDelete` locks on both the `db` container app and the `vol3ovvqiybthkb6` storage account. No deploy tool (azd, aspire deploy, Bicep, ARM) can delete or recreate these resources without first explicitly removing the lock. Applied via:

```bash
az lock create --name do-not-delete-db \
  --resource-group rg-sstudio-prod \
  --resource db --resource-type Microsoft.App/containerApps \
  --lock-type CanNotDelete

az lock create --name do-not-delete-db-storage \
  --resource-group rg-sstudio-prod \
  --resource vol3ovvqiybthkb6 --resource-type Microsoft.Storage/storageAccounts \
  --lock-type CanNotDelete
```

### Layer 3: Preprovision Hook (added to azure.yaml)
`azure.yaml` now includes a `preprovision` hook that runs `scripts/pre-deploy-check.sh` before any `azd` operation. The script verifies:
- Resource locks exist (at least 2)
- The `db` container app exists
- The current revision has an AzureFile volume mount
- The storage account exists
- The file share exists and is non-empty

If any check fails, the hook exits 1 and blocks the deploy.

### Layer 4: Runbook Lock Verification (added to deploy-runbook.md)
Step 5 added to the pre-deploy safety checklist: verify resource locks exist before deploying. Includes the `az lock list` command and remediation instructions if locks are missing.

### Layer 5: Managed Database Migration Path (documented)
A "Future: Managed Database Migration" section added to `docs/deploy-runbook.md` documenting the path to Azure PostgreSQL Flexible Server. This eliminates the volume-mount fragility entirely:
- Automatic daily backups with 35-day retention
- Point-in-time restore
- No volume mounts to lose
- ~$17/month estimated cost
- AppHost changes: `AddAzurePostgresFlexibleServer("db").RunAsContainer(...)`

## The 5-Layer Defense Model

```
Layer 1: BACKUP        --> Recovery possible even if all else fails
Layer 2: LOCK          --> Azure blocks deletion of DB + storage resources
Layer 3: HOOK          --> azd refuses to proceed if checks fail
Layer 4: VERIFICATION  --> Human confirms locks exist before deploying
Layer 5: MANAGED DB    --> Eliminates the vulnerability class entirely (future)
```

Each layer is independent. Any single layer would have prevented the 2025-07-25 incident. All 5 together make repeat data loss from deployment errors extremely unlikely.

## Files Changed

| File | Change |
|------|--------|
| `azure.yaml` | Added `preprovision` hook pointing to safety check script |
| `scripts/pre-deploy-check.sh` | New: automated pre-deploy safety verification |
| `docs/deploy-runbook.md` | Added Step 5 (lock verification) + managed DB migration section |
| Azure resources | CanNotDelete locks on `db` container app and `vol3ovvqiybthkb6` storage account |

## Why

Because losing production data once is a wake-up call. Losing it twice is negligence. These 5 layers ensure that even if one defense fails, the others catch it. The managed database migration (Layer 5) is the permanent architectural fix that makes the other layers unnecessary for this specific risk -- but we keep them all because defense in depth is not optional.
