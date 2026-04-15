# Deploy Runbook

> **WARNING — READ THIS ENTIRE SECTION BEFORE RUNNING ANY DEPLOY COMMAND**
>
> On 2025-07-25, a production deploy destroyed all user data because the database
> container was recreated without its volume mount and there was no backup.
> The checklist below exists to prevent this from EVER happening again.
> **Skipping any step is forbidden.**

---

## MANDATORY Pre-Deploy Safety Checklist

**Every step below MUST pass before ANY production deploy command is executed.**  
**Failure of ANY step is a HARD STOP — do not proceed, do not "try anyway."**

### Step 0: NEVER mix deploy tools

**Use EITHER `azd deploy` OR `aspire deploy` — NEVER both in the same session.**

Why: `azd` and `aspire deploy` manage Azure resources through different Bicep pipelines.
When one tool recreates a container that the other tool provisioned, stateful configuration
(like volume mounts) can be silently dropped. This is exactly what caused the 2025-07-25
data loss incident. Pick one tool. Stick with it.

**Current recommendation:** Use `azd deploy` until `aspire deploy` exits preview.

### Step 1: Back up the production database

```bash
# Option A: pg_dump via container exec
az containerapp exec \
  --name db --resource-group rg-sstudio-prod \
  --command "pg_dump -U postgres sentencestudio" > backup-$(date +%Y%m%d-%H%M%S).sql

# Verify the backup is real (not empty, contains table definitions)
head -50 backup-*.sql   # Should show CREATE TABLE / COPY statements
wc -l backup-*.sql      # Should be substantial (hundreds+ of lines)
```

```bash
# Option B: Download the Azure File share directly
az storage file download-batch \
  --account-name vol3ovvqiybthkb6 \
  --source db-sentencestudioapphost8351ffded3dbdata \
  --destination ./backup-$(date +%Y%m%d-%H%M%S)/
```

**Pass condition:** Backup file is non-empty AND contains expected SQL/data.  
**If this fails:** STOP. Fix the backup before proceeding. You cannot deploy without a backup.

### Step 2: Verify stateful containers have volume mounts

```bash
az containerapp revision list \
  --name db --resource-group rg-sstudio-prod \
  --query "[0].{revision:name, volumes:properties.template.volumes}" -o json
```

**Pass condition:** The `volumes` array contains an entry with:
- `storageType: AzureFile`
- `storageName: db-sentencestudioapphost8351ffde`

**If the volumes array is empty or null: DO NOT DEPLOY.** The DB container will lose all data.

### Step 3: Verify the Azure File share exists and has data

```bash
az storage file list \
  --account-name vol3ovvqiybthkb6 \
  --share-name db-sentencestudioapphost8351ffded3dbdata \
  --output table
```

**Pass condition:** File list is non-empty (PostgreSQL data files present).

### Step 4: Verify the ACA environment storage mount exists

```bash
az containerapp env storage show \
  --name cae-3ovvqiybthkb6 --resource-group rg-sstudio-prod \
  --storage-name db-sentencestudioapphost8351ffde
```

**Pass condition:** Returns storage mount configuration (not a 404/error).

### Step 5: Verify resource locks exist

```bash
az lock list --resource-group rg-sstudio-prod --output table
```

**Pass condition:** Output shows at least two locks:
- `do-not-delete-db` (CanNotDelete) on `Microsoft.App/containerApps/db`
- `do-not-delete-db-storage` (CanNotDelete) on `Microsoft.Storage/storageAccounts/vol3ovvqiybthkb6`

**If no locks found: STOP.** Create them before deploying:

```bash
az lock create --name do-not-delete-db \
  --resource-group rg-sstudio-prod \
  --resource db --resource-type Microsoft.App/containerApps \
  --lock-type CanNotDelete \
  --notes "Protect production database from accidental deletion by deploy tools"

az lock create --name do-not-delete-db-storage \
  --resource-group rg-sstudio-prod \
  --resource vol3ovvqiybthkb6 --resource-type Microsoft.Storage/storageAccounts \
  --lock-type CanNotDelete \
  --notes "Protect production database file share from accidental deletion"
```

### All checks passed? Proceed to deploy.

---

## MANDATORY Post-Deploy Verification

**Run these IMMEDIATELY after every deploy completes. Do not walk away until all pass.**

### Post-1: Verify new revision has volume mount

```bash
az containerapp revision list \
  --name db --resource-group rg-sstudio-prod \
  --query "[0].{revision:name, volumes:properties.template.volumes}" -o json
```

**Pass condition:** Same as Step 2 — `AzureFile` volume with correct storage name.  
**If this fails:** The deploy dropped the volume mount. The data on the file share is still intact
but the container cannot see it. **Do not write any new data.** Fix the revision immediately
by re-adding the volume mount via `az containerapp update` or redeploying with the correct
AppHost configuration.

### Post-2: Verify database is accessible and has data

```bash
az containerapp exec \
  --name db --resource-group rg-sstudio-prod \
  --command "psql -U postgres -d sentencestudio -c 'SELECT count(*) FROM \"Users\";'"
```

**Pass condition:** Returns a row count > 0 (or expected count based on known state).  
**If this returns 0 or errors:** The database is empty or unreachable. Restore from backup immediately.

### Post-3: API health check

```bash
curl -sf https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/health && echo "OK"
```

**Pass condition:** Returns HTTP 200.

---

## "Publish" = Azure + DX24

When the Captain says **"publish"**, **"deploy"**, or **"push to my phone"**, execute BOTH steps below. They are not optional. Both targets must point at the **same Azure API**.

---

## Step 1: Deploy to Azure

### Option A: `azd deploy` (stable)

```bash
# Ensure VPN is OFF (management.azure.com times out on VPN)
cd /Users/davidortinau/work/SentenceStudio
azd deploy
```

### Option B: `aspire deploy` (Preview)

```bash
# Ensure VPN is OFF
cd /Users/davidortinau/work/SentenceStudio
aspire deploy -e production
```

**How it works:** The AppHost declares `AddAzureContainerAppEnvironment("aca-env").WithAzdResourceNaming()` which makes `aspire deploy` target the same resources in `rg-sstudio-prod` that `azd` created. Deployment state is stored in `~/.aspire/deployments/`.

**Useful flags:**
- `aspire deploy --log-level debug` — verbose output for troubleshooting
- `aspire deploy --clear-cache` — reset deployment state and reprompt for Azure config
- `aspire deploy --no-build` — skip build if you already built

**Note:** `aspire deploy` is Preview (CLI 13.3.0-preview.1). If it fails, fall back to `azd deploy`.

**Expected output:** All services succeed (api, webapp, cache, db, marketing, workers).  
**Webapp URL:** `https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io/`  
**API URL:** `https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/`

---

## Step 2: Build & Deploy iOS to DX24

DX24 is Captain's iPhone 15 Pro. Device ID: `CF4F94E3-A1C9-5617-A089-9ABB0110A09F`

### 2a. Switch to .NET 11 Preview 3 SDK (required for Xcode 26.3)

```bash
cd /Users/davidortinau/work/SentenceStudio
cp global.json global.json.bak
cat > global.json << 'EOF'
{
  "sdk": {
    "version": "11.0.100-preview.3.26209.122",
    "rollForward": "latestFeature",
    "allowPrerelease": true
  }
}
EOF
```

### 2b. Build Release with Azure API URL

```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64
```

### 2c. Install and launch on DX24

```bash
xcrun devicectl device install app \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  src/SentenceStudio.iOS/bin/Release/net10.0-ios/ios-arm64/SentenceStudio.iOS.app

xcrun devicectl device process launch \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  com.simplyprofound.sentencestudio
```

### 2d. Restore global.json

```bash
cp global.json.bak global.json && rm global.json.bak
```

---

## Local Development Build (NOT for publish)

For local Aspire development only. Points at localhost, requires Aspire running.

```bash
# Debug build — uses appsettings.json (localhost:5081)
dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net10.0-ios -c Debug -p:RuntimeIdentifier=ios-arm64
```

---

## Future: Managed Database Migration

> **This is the RECOMMENDED long-term solution.** Migrating from the containerized Postgres +
> Azure File share to Azure Database for PostgreSQL Flexible Server eliminates the entire class
> of volume-mount data loss vulnerabilities.

### Why Managed Postgres

The current architecture is inherently fragile: any deploy tool that regenerates the `db` container
revision without preserving the Azure File share volume mount will start an empty database. A managed
database service is completely decoupled from container deployments — no file shares, no volume mounts,
no risk of silent data loss from a routine deploy.

### What You Get

| Capability | Current (Container + File Share) | Managed (Flexible Server) |
|------------|----------------------------------|---------------------------|
| Automatic backups | None | Daily, 35-day retention, configurable |
| Point-in-time restore | Impossible | Any second within retention window |
| Deploy safety | Volume mount can be silently dropped | Not affected by app container deploys |
| Scaling | Manual container limits | Built-in vertical/horizontal scaling |
| High availability | None | Zone-redundant option |
| Monitoring | Manual | Azure Monitor integration |

### Estimated Cost

~$17/month for Burstable B1ms (1 vCore, 2GB RAM, 32GB storage). Trivial compared to the value
of production data.

### AppHost Changes Required

```csharp
// BEFORE (current — fragile):
var postgresServer = builder.AddPostgres("db")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .PublishAsAzureContainerApp((infra, app) => { /* volume mount config */ });

// AFTER (managed — durable):
var postgresServer = builder.AddAzurePostgresFlexibleServer("db")
    .RunAsContainer(c => c
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume());

var postgres = postgresServer.AddDatabase("sentencestudio");
```

- `.AddAzurePostgresFlexibleServer()` provisions a managed Azure PostgreSQL Flexible Server in production
- `.RunAsContainer()` preserves the local dev experience (Docker container with named volume)
- No file share, no volume mount callback, no `PublishAsAzureContainerApp` override needed
- Connection string is automatically injected via Aspire service discovery

### Defense in Depth

Even with a managed database, resource locks should remain in place (defense in depth):
- Lock the Flexible Server resource itself (`CanNotDelete`)
- Lock the resource group if appropriate
- Keep the preprovision hook in `azure.yaml` — adapt checks for the managed server

### Migration Steps

1. Provision the Flexible Server (can run in parallel with existing container)
2. Export data: `pg_dump` via `az containerapp exec`
3. Import to Flexible Server: `pg_restore` to the managed endpoint
4. Update AppHost to use `AddAzurePostgresFlexibleServer`
5. Deploy and verify
6. Decommission old container + file share after 1 week of clean operation

See `.squad/decisions/inbox/zoe-production-data-safety.md` for the full migration plan.

---

## Common Issues

| Problem | Fix |
|---------|-----|
| `azd deploy` times out | Turn off VPN, retry |
| `aspire deploy` creates wrong RG | Check `~/.aspire/deployments/.../production.json` has `rg-sstudio-prod` |
| `aspire deploy` principal error | Ensure AppHost has `AddAzureContainerAppEnvironment` |
| `aspire deploy` stale state | Run `aspire deploy --clear-cache` to reset |
| Xcode version mismatch (26.2 vs 26.3) | Use .NET 11 Preview 3 SDK (step 2a) |
| Device locked error on install | Unlock DX24, retry |
| LOCAL ribbon on phone | Built with Debug config — rebuild with Release + env var (step 2b) |
| Phone app can't reach API | Missing `services__api__https__0` env var at build time |
| DB data lost after deploy | **CRITICAL**: Check Post-Deploy verification above. If volume mount is missing, data is likely still on the Azure File share — fix the revision ASAP. If empty, restore from backup. |
| Mixed azd + aspire deploy | **NEVER do this.** See "NEVER mix deploy tools" warning above. If you already mixed them, run ALL pre-deploy checks before any further action. |
