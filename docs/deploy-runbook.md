# Deploy Runbook

> **Architecture: Azure PostgreSQL Flexible Server (managed)**
>
> As of 2025-07-26, production uses **Azure Database for PostgreSQL — Flexible Server**,
> provisioned by the Aspire AppHost via `AddAzurePostgresFlexibleServer("db")`.
> This eliminates the container + volume-mount data loss vulnerabilities that caused
> the 2025-07-25 incident. The managed service provides automatic backups, point-in-time
> restore, and is completely decoupled from container app deploys.

---

## Current Production Stack (as of 2026-06-07)

Production was migrated to the **business tenant** so the server hosts can authenticate
keyless to Azure AI Foundry (the Foundry resource has local-auth disabled by policy, and
managed identities cannot authenticate cross-tenant). The old personal-tenant stack
(`rg-sstudio-prod`, centralus, `livelyforest-b32e7d63`) is retained as a rollback path
pending decommission — do **not** target it.

| Thing | Value |
|---|---|
| Subscription (business) | `66f9fa8f-604f-4688-bec1-16ff9a86a8e5` |
| Resource group | `rg-sstudio-prod-biz` |
| azd environment | `sstudio-prod-biz` |
| Region | `westus3` |
| ACA environment | `cae-rsn72awybem6s` (domain `agreeablesky-76d2f81f.westus3.azurecontainerapps.io`) |
| Container Registry | `acrrsn72awybem6s` |
| PostgreSQL Flexible Server | `db-rsn72awybem6s` (db `sentencestudio`) |
| Key Vault | `dbkv-rsn72awybem6s` |
| Log Analytics | `law-rsn72awybem6s` |
| App Insights | `appi-sstudio-biz` |
| Managed identity (UAMI) | `mi-rsn72awybem6s` |
| Storage | `storagersn72awybem6s` (blob container `media`) |
| **API** | `https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |
| **WebApp** | `https://webapp.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |
| **Marketing** | `https://marketing.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |
| Aspire dashboard | `https://aspire-dashboard.ext.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |

**AI / Azure AI Foundry:** server hosts call Foundry resource **`daortin-sstudio-eus2`**
(eastus2, rg `rg-daortin-4819`, same business sub). Endpoint
`https://daortin-sstudio-eus2.openai.azure.com/openai/v1` is injected as the
`AI__OpenAI__Endpoint` env var by `AppHost.cs`. Auth is **keyless via Entra** — the prod
UAMI `mi-rsn72awybem6s` holds the **Cognitive Services OpenAI User** role on that account.
This one account carries chat (`gpt-5-mini` fast / `gpt-5` reasoning) plus realtime +
transcribe model availability. See memory `ai-foundry-model-tiers`.

Production deploys must **not** require an `openaikey` / `AI__OpenAI__ApiKey` parameter
for server hosts. That legacy key is only for local direct-client fallback paths; if
`azd deploy` asks for `openaikey`, stop and fix AppHost/deploy wiring instead of copying
or inventing a secret.

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
  --name db-rsn72awybem6s --resource-group rg-sstudio-prod-biz \
  --admin-user <admin> --admin-password <password> \
  --database-name sentencestudio \
  --querytext "SELECT count(*) FROM \"Users\";"

# Full backup: use az postgres flexible-server backup (built-in, daily, 35-day retention)
az postgres flexible-server backup list \
  --resource-group rg-sstudio-prod-biz --server-name db-rsn72awybem6s
```

**Pass condition:** Backup list is non-empty, or you've verified the built-in backup schedule is active.
**If this fails:** STOP. Verify the Flexible Server is healthy before proceeding.

### Step 2: Verify the Flexible Server is healthy

```bash
az postgres flexible-server show \
  --name db-rsn72awybem6s --resource-group rg-sstudio-prod-biz \
  --query "{name:name, state:state, version:version, sku:sku.name}" -o json
```

**Pass condition:** `state` is `Ready`.

### Step 3: Verify resource locks exist

```bash
az lock list --resource-group rg-sstudio-prod-biz --output table
```

**Pass condition:** Output shows a lock on the Flexible Server resource (CanNotDelete).

**If no lock on the Flexible Server:** Create one:

```bash
az lock create --name do-not-delete-postgres \
  --resource-group rg-sstudio-prod-biz \
  --resource db-rsn72awybem6s \
  --resource-type Microsoft.DBforPostgreSQL/flexibleServers \
  --lock-type CanNotDelete \
  --notes "Protect production managed PostgreSQL from accidental deletion"
```

### Step 4: Validate mobile migrations (if PR includes migration changes)

**If the deploy includes a new or modified EF Core migration, RUN THIS:**

```bash
bash scripts/validate-mobile-migrations.sh
```

**Pass condition:** Script exits with code 0 and prints `✅ Mobile migrations validated`.

**If this fails:** DO NOT DEPLOY. Fix the migration. Common failures:
- Unsupported SQLite operation (e.g., ALTER COLUMN) → use PatchMissingColumnsAsync pattern
- Missing column/table after migration → migration threw exception or was silently skipped
- Sanity check failed → critical schema piece missing

This gate catches iOS/Android-specific migration failures that desktop/server builds may not exhibit. It builds Mac Catalyst DEBUG, launches via `maui devflow`, and scans startup logs for migration errors.

**Expected non-fatal warnings (validated 2026-06-24):** the script may print
`⚠️ Could not fetch native logs via maui devflow` and
`⚠️ Schema sanity check PASSED message not found` while still exiting 0 — this happens when
the `maui devflow` agent connection is flaky and native logs can't be pulled. A **clean
build + launch with no `SQLite Error` / `no such column` / `MigrateAsync failed` in the
captured logs is still a pass** for a purely additive migration (`AddColumn` only). For
riskier migrations (table rebuilds, data moves), re-run until the agent connects and the
`Schema sanity check PASSED` line appears, or validate manually on a Catalyst run.

**Skip this step ONLY if:** The deploy contains NO changes to files under `src/SentenceStudio.Shared/Migrations/`.

### All checks passed? Proceed to deploy.

---

## MANDATORY Post-Deploy Verification

**A deploy is NOT complete until validation passes. `azd deploy` exit code 0 means the upload worked — not that the system works.**

### Step 5: Run the automated validation script

```bash
./scripts/post-deploy-validate.sh
```

**This script covers:**
- Phase 1: Infrastructure health (revision status, active=latest, no crash loops, DB connectivity, endpoint codes, EF migrations)
- Blazor Server circuit safety (`webapp` ingress sticky sessions must be enabled; otherwise `_blazor` WebSocket/long-polling requests can 404 across replicas)
- Phase 2: Functional smoke test (auth flow, protected endpoints, webapp renders)
- Phase 4: Regression check (core API endpoints, marketing site)

**Exit code 0 = PASS. Non-zero = FAIL — investigate immediately.**

To skip the 30-second startup wait (e.g., if you've already waited):
```bash
./scripts/post-deploy-validate.sh --skip-wait
```

To run only infrastructure checks (faster):
```bash
./scripts/post-deploy-validate.sh --phase1-only
```

To enable auth flow tests, set the test account credentials:
```bash
DEPLOY_TEST_PASSWORD="..." ./scripts/post-deploy-validate.sh
```

### Step 5a: Re-apply webapp sticky sessions (recurring — `azd deploy` resets it)

**Known drift (validated publish 2026-06-24):** `azd deploy` updates the `webapp`
Container App from the Aspire-generated manifest, which does **not** declare ingress
sticky sessions. As a result, the webapp ingress affinity is reset to `null` on **every**
deploy, and the post-deploy validator's "Blazor Server circuit affinity" check FAILs.
Because `webapp` scales to multiple replicas (`maxReplicas: 10`), a disabled affinity lets
Blazor Server `_blazor` WebSocket/long-polling requests 404 across replicas — interactive
pages (e.g. vocabulary word edit) break under load.

**Fix (idempotent, non-destructive — re-run after every `azd deploy`):**
```bash
az containerapp ingress sticky-sessions set \
  -n webapp -g rg-sstudio-prod-biz --affinity sticky

# verify
az containerapp show -n webapp -g rg-sstudio-prod-biz \
  --query "properties.configuration.ingress.stickySessions.affinity" -o tsv   # -> sticky
```
Then re-run `./scripts/post-deploy-validate.sh --skip-wait` and confirm 0 FAIL.

> **Permanent fix (TODO):** declare `stickySessions.affinity = "sticky"` on the `webapp`
> ingress in the AppHost/Bicep so `azd deploy` stops reverting it. Until then this is a
> mandatory manual step on each deploy.

### Step 6: Change-specific validation (manual)

After the automated script passes, verify the **specific change you deployed**:

1. What changed? (DB migration, new endpoint, UI change, config change?)
2. Is the change live in the running system? (Query the DB, hit the endpoint, load the page)
3. Does it behave correctly? (Expected data shape, correct UI, proper error handling)

See `docs/specs/post-deploy-validation.md` Phase 3 for common validation patterns.

**Verify a migration applied to production (recipe — validated 2026-06-24):**
The prod DB connection string lives in Key Vault; pull it and query with `psql` directly —
no need to know the admin password by heart:
```bash
cs=$(az keyvault secret show --vault-name dbkv-rsn72awybem6s \
  --name connectionstrings--sentencestudio --query value -o tsv)
host=$(echo "$cs" | grep -oE 'Host=[^;]+' | cut -d= -f2)
db=$(echo   "$cs" | grep -oE 'Database=[^;]+' | cut -d= -f2)
user=$(echo "$cs" | grep -oE 'Username=[^;]+' | cut -d= -f2)
pass=$(echo "$cs" | grep -oE 'Password=[^;]+' | cut -d= -f2)

# Was the migration recorded?
PGPASSWORD="$pass" psql "host=$host dbname=$db user=$user sslmode=require" -t \
  -c "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 5;"

# Do the new columns exist? (adjust table/column names)
PGPASSWORD="$pass" psql "host=$host dbname=$db user=$user sslmode=require" -t \
  -c "SELECT column_name FROM information_schema.columns WHERE table_name='ExampleSentence';"
```
> Note: a healthy API revision (active=latest, no crash loop, DB connectivity PASS) is
> already strong evidence the migration applied — EF `MigrateAsync` runs at startup, so a
> failed migration crash-loops the revision and it never becomes active. The query above is
> the definitive confirmation.

### Legacy individual checks (still valid for manual spot-checking)

**DB connectivity:**
```bash
az postgres flexible-server execute \
  --name db-rsn72awybem6s --resource-group rg-sstudio-prod-biz \
  --admin-user <admin> --admin-password <password> \
  --database-name sentencestudio \
  --querytext "SELECT count(*) FROM \"Users\";"
```

**API endpoint check:**
```bash
curl -s -o /dev/null -w "%{http_code}" \
  -X POST https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"x","password":"x"}'
# Expected: 400 or 401 (API is alive, DB reachable)
# Bad: 500/502/503 (API crashed or can't reach DB)
```

**EF Core migrations:**
```bash
az containerapp logs show --name api --resource-group rg-sstudio-prod-biz --tail 50 | grep -i migrat
```

**AI / Foundry keyless auth (confirm chat calls succeed, no 401s):**
```bash
az containerapp logs show --name api --resource-group rg-sstudio-prod-biz --tail 50 \
  | grep -iE "daortin-sstudio-eus2|chat/completions|401|unauthor"
# Good: POST https://daortin-sstudio-eus2.openai.azure.com/openai/deployments/gpt-5-mini/...
# Bad:  401 / Unauthorized (UAMI missing the Cognitive Services OpenAI User role)
```

---

## "Publish" = Azure + DX24

When the Captain says **"publish"**, **"deploy"**, or **"push to my phone"**, execute BOTH steps below. They are not optional. Both targets must point at the **same Azure API**.

---

## Step 1: Deploy to Azure

### Option A: `azd deploy` (stable)

```bash
# Ensure VPN is OFF (management.azure.com times out on VPN)
cd /Users/davidortinau/work/SentenceStudio
azd deploy -e sstudio-prod-biz
```

### Option B: `aspire deploy` (Preview)

```bash
# Ensure VPN is OFF
cd /Users/davidortinau/work/SentenceStudio
aspire deploy -e production
```

**How it works:** The AppHost declares `AddAzureContainerAppEnvironment("aca-env").WithAzdResourceNaming()` which makes `aspire deploy` target the same resources in `rg-sstudio-prod-biz` that `azd` created. Deployment state is stored in `~/.aspire/deployments/`.

**Useful flags:**
- `aspire deploy --log-level debug` — verbose output for troubleshooting
- `aspire deploy --clear-cache` — reset deployment state and reprompt for Azure config
- `aspire deploy --no-build` — skip build if you already built

**Note:** `aspire deploy` is Preview (CLI 13.3.0-preview.1). If it fails, fall back to `azd deploy`.

**Expected output:** All services succeed (api, webapp, cache, marketing, workers).  
**Webapp URL:** `https://webapp.agreeablesky-76d2f81f.westus3.azurecontainerapps.io/`  
**API URL:** `https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io/`

---

## Step 2: Build & Deploy iOS to DX24

DX24 is Captain's iPhone 15 Pro. Device ID: `CF4F94E3-A1C9-5617-A089-9ABB0110A09F`

### 2a. SDK requirements (no swap needed)

> **As of June 2026, the repo targets `net11.0-ios` directly** — no `global.json`
> swap is required. Captain's local `global.json` already pins to a `net11.0`
> preview SDK (currently preview 4, `11.0.100-preview.4.26230.115`) with
> `rollForward: latestPatch` and `allowPrerelease: true`. The net11 preview 4 MAUI
> workload ships iOS SDK packs that support Xcode 26.3+.
>
> If a build errors with `This version of .NET for iOS (26.2.xxxx) requires Xcode 26.2`,
> look at the pack version in the error:
> - `26.2.10xxx` (no suffix) = **net10 pack** — SDK resolution fell back to net10
>   (this should not happen on the current branch; check `dotnet --version` reports
>   a `11.0.100-preview.*` SDK from this directory)
> - `26.2.11xxx-net11-pN` = **net11 preview pack**, Xcode 26.3 compatible — correct
>
> Historical note: prior to migrating the iOS head to `net11.0-ios`, this step
> required a temporary swap to `11.0.100-preview.3.26209.122`. That swap is no
> longer needed.

### 2b. Build Release with Azure API URL

```bash
services__api__https__0=https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net11.0-ios -c Release -p:RuntimeIdentifier=ios-arm64
```

> The committed `src/SentenceStudio.AppLib/appsettings.Production.json` already points
> `Services:api`/`Services:web` at the westus3 URLs, so Release device builds resolve the
> correct API even without the env var. Passing `services__api__https__0` at build time is
> belt-and-suspenders and matches the historical procedure — keep it.

### 2c. Install and launch on DX24

> **Before running install:** wake DX24 and confirm unlocked. **Budget for one retry** on the `devicectl install` command — first attempt frequently fails with `NWError 57` due to deep-sleep tunnel teardown, second attempt succeeds. This is a known, validated pattern (publishes #6–#9). See [`.squad/skills/maui-ios-dx24-install/SKILL.md`](../.squad/skills/maui-ios-dx24-install/SKILL.md) for the full preemptive procedure and recovery path.

```bash
xcrun devicectl device install app \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  src/SentenceStudio.iOS/bin/Release/net11.0-ios/ios-arm64/SentenceStudio.iOS.app

xcrun devicectl device process launch \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  com.simplyprofound.sentencestudio
```

> **Transient devicectl errors:** The first install attempt sometimes fails with
> `Network.NWError error 57 - Socket is not connected` or
> `ControlChannelConnectionError error 1`. These are **transient** — wait a few
> seconds and retry the same command. It almost always succeeds on the second try.
> Verify the device is reachable first with `xcrun devicectl list devices`
> (DX24 should show `available (paired)`).
>
> **`FBSOpenApplicationErrorDomain error 7 (Locked)` on launch:** The iPhone
> is screen-locked. Unlock it and either retry the launch command or just tap
> the app icon. Install already succeeded — only launch was blocked.

---

## Local Development Build (NOT for publish)

For local Aspire development only. Points at localhost, requires Aspire running.

```bash
# Debug build — uses appsettings.json (localhost:5081)
dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net11.0-ios -c Debug -p:RuntimeIdentifier=ios-arm64
```



## Common Issues

| Problem | Fix |
|---------|-----|
| `azd deploy` times out | Turn off VPN, retry |
| `azd deploy` targets wrong stack | Pass `-e sstudio-prod-biz` (the new business-tenant env); confirm `azd env list` shows it as DEFAULT |
| `azd provision` fails on Flexible Server | Check subscription quota AND offer restrictions (`LocationIsOfferRestricted`) for PostgreSQL Flexible Servers in the region — westus3 is the known-open region for this sub |
| `aspire deploy` creates wrong RG | Check `~/.aspire/deployments/.../production.json` has `rg-sstudio-prod-biz` |
| `aspire deploy` principal error | Ensure AppHost has `AddAzureContainerAppEnvironment` |
| `aspire deploy` stale state | Run `aspire deploy --clear-cache` to reset |
| AI calls 401 / Unauthorized | UAMI `mi-rsn72awybem6s` missing **Cognitive Services OpenAI User** on `daortin-sstudio-eus2`; or role assignment hasn't propagated yet (wait 5–10 min) |
| Xcode version mismatch (26.2 vs 26.3) | Verify `dotnet --version` reports a `11.0.100-preview.*` SDK from this directory and that the pack version in the error is `26.2.11*-net11-pN`, NOT `26.2.10*` |
| Build keeps picking net10 iOS pack | Confirm iOS csproj `<TargetFramework>` is `net11.0-ios`; confirm `dotnet --version` reports `11.0.100-preview.*`; delete `obj/` under `src/SentenceStudio.iOS/` and rebuild |
| `devicectl` install fails with `Socket is not connected` | Transient — retry once after a few seconds. Check `xcrun devicectl list devices` shows DX24 `available (paired)` |
| Device locked error on install | Unlock DX24, retry |
| LOCAL ribbon on phone | Built with Debug config — rebuild with Release + env var (step 2b) |
| Phone app can't reach API | Missing `services__api__https__0` env var at build time, or stale `appsettings.Production.json` URL |
| EF Core can't connect to Flexible Server | Check firewall rules allow ACA subnet; verify connection string in Aspire dashboard |
| Mixed azd + aspire deploy | **NEVER do this.** See "NEVER mix deploy tools" warning above. |

---

## Legacy: old personal-tenant stack (pending decommission)

> The original production stack lives in `rg-sstudio-prod` (personal tenant, centralus,
> ACA domain `livelyforest-b32e7d63`, resource token `3ovvqiybthkb6`). It is **retained as a
> rollback path** after the 2026-06 migration to `rg-sstudio-prod-biz` and is no longer the
> deploy target. When confident in the new stack, decommission it: take a final `pg_dump`,
> remove `CanNotDelete` locks, delete the RG. Also remove the temporary migration grants
> (firewall rule `migrate-client` on both old and new Postgres servers; Key Vault Secrets
> User grant on the old `dbkv-3ovvqiybthkb6`).
>
> Separately, the old containerized `db` app + Azure File share (`vol3ovvqiybthkb6`) from
> before the Flexible Server migration may still exist there with `CanNotDelete` locks;
> they go away when that RG is deleted.
