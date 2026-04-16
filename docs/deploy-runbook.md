# Deploy Runbook

> **Architecture: Azure PostgreSQL Flexible Server (managed)**
>
> As of 2025-07-26, production uses **Azure Database for PostgreSQL — Flexible Server**,
> provisioned by the Aspire AppHost via `AddAzurePostgresFlexibleServer("db")`.
> This eliminates the container + volume-mount data loss vulnerabilities that caused
> the 2025-07-25 incident. The managed service provides automatic backups, point-in-time
> restore, and is completely decoupled from container app deploys.

---

## MANDATORY Pre-Deploy Safety Checklist

**Every step below MUST pass before ANY production deploy command is executed.**  
**Failure of ANY step is a HARD STOP — do not proceed, do not "try anyway."**

### Step 0: NEVER mix deploy tools

**Use EITHER `azd deploy` OR `aspire deploy` — NEVER both in the same session.**

Why: `azd` and `aspire deploy` manage Azure resources through different Bicep pipelines.
Pick one tool. Stick with it.

**Current recommendation:** Use `azd deploy` until `aspire deploy` exits preview.
For infrastructure changes (like provisioning the Flexible Server), use `azd provision` first.

### Step 1: Back up the production database

```bash
# pg_dump via the Flexible Server's public endpoint
# (requires psql client and network access — use az postgres flexible-server connect)
az postgres flexible-server execute \
  --name <flexible-server-name> --resource-group rg-sstudio-prod \
  --admin-user <admin> --admin-password <password> \
  --database-name sentencestudio \
  --querytext "SELECT count(*) FROM \"Users\";"

# Full backup: use az postgres flexible-server backup (built-in, daily, 35-day retention)
az postgres flexible-server backup list \
  --resource-group rg-sstudio-prod --server-name <flexible-server-name>
```

**Pass condition:** Backup list is non-empty, or you've verified the built-in backup schedule is active.
**If this fails:** STOP. Verify the Flexible Server is healthy before proceeding.

### Step 2: Verify the Flexible Server is healthy

```bash
az postgres flexible-server show \
  --name <flexible-server-name> --resource-group rg-sstudio-prod \
  --query "{name:name, state:state, version:version, sku:sku.name}" -o json
```

**Pass condition:** `state` is `Ready`.

### Step 3: Verify resource locks exist

```bash
az lock list --resource-group rg-sstudio-prod --output table
```

**Pass condition:** Output shows a lock on the Flexible Server resource (CanNotDelete).

**If no lock on the Flexible Server:** Create one:

```bash
az lock create --name do-not-delete-postgres \
  --resource-group rg-sstudio-prod \
  --resource <flexible-server-name> \
  --resource-type Microsoft.DBforPostgreSQL/flexibleServers \
  --lock-type CanNotDelete \
  --notes "Protect production managed PostgreSQL from accidental deletion"
```

### All checks passed? Proceed to deploy.

---

## MANDATORY Post-Deploy Verification

**Run these IMMEDIATELY after every deploy completes. Do not walk away until all pass.**

### Post-1: Verify Flexible Server connectivity

```bash
az postgres flexible-server execute \
  --name <flexible-server-name> --resource-group rg-sstudio-prod \
  --admin-user <admin> --admin-password <password> \
  --database-name sentencestudio \
  --querytext "SELECT count(*) FROM \"Users\";"
```

**Pass condition:** Returns a row count (0 is OK for first deploy — schema created by EF Core migrations).

### Post-2: API health check

```bash
curl -sf https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/health && echo "OK"
```

**Pass condition:** Returns HTTP 200.

### Post-3: Verify EF Core migrations ran

Check API logs for migration output:

```bash
az containerapp logs show --name api --resource-group rg-sstudio-prod --tail 50 | grep -i migrat
```

**Pass condition:** Logs show "Applying migration" or "Database is up to date".

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



## Common Issues

| Problem | Fix |
|---------|-----|
| `azd deploy` times out | Turn off VPN, retry |
| `azd provision` fails on Flexible Server | Check subscription quota for PostgreSQL Flexible Servers in the region |
| `aspire deploy` creates wrong RG | Check `~/.aspire/deployments/.../production.json` has `rg-sstudio-prod` |
| `aspire deploy` principal error | Ensure AppHost has `AddAzureContainerAppEnvironment` |
| `aspire deploy` stale state | Run `aspire deploy --clear-cache` to reset |
| Xcode version mismatch (26.2 vs 26.3) | Use .NET 11 Preview 3 SDK (step 2a) |
| Device locked error on install | Unlock DX24, retry |
| LOCAL ribbon on phone | Built with Debug config — rebuild with Release + env var (step 2b) |
| Phone app can't reach API | Missing `services__api__https__0` env var at build time |
| EF Core can't connect to Flexible Server | Check firewall rules allow ACA subnet; verify connection string in Aspire dashboard |
| Mixed azd + aspire deploy | **NEVER do this.** See "NEVER mix deploy tools" warning above. |

---

## Legacy: Containerized Postgres (deprecated)

> The old `db` container app and Azure File share (`vol3ovvqiybthkb6`) may still exist in
> `rg-sstudio-prod` with CanNotDelete locks. They are no longer referenced by the AppHost.
> Leave them in place until you're confident the managed Flexible Server is stable, then
> remove the locks and delete them to avoid ongoing storage costs.
