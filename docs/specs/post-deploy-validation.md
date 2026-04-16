# Post-Deploy Validation Spec

> **Status:** ACTIVE
> **Author:** Zoe (Lead)
> **Date:** 2025-07-27
> **Trigger:** Captain identified that deploys were being declared successful based solely on `azd deploy` exit code 0, without verifying the deployed system actually works. API was crash-looping for 15+ minutes undetected.

---

## Core Principle

**A deploy is NOT complete until validation passes.**

`azd deploy` exit code 0 means the **upload** succeeded. It says nothing about whether the system **works**. Every deploy must be followed by the full validation sequence below. If ANY check fails, the deploy is **FAILED** and must be investigated before anyone claims success.

---

## Timing

After `azd deploy` or `aspire deploy` completes:

| Wait | Reason |
|------|--------|
| **30 seconds** | Container Apps need time to pull images, start containers, run EF Core migrations |
| **60 seconds** | If first health check fails, wait another 30s and retry once |
| **After 90 seconds** | If still failing, the deploy is broken. Do NOT wait longer hoping it will fix itself. |

---

## Phase 1: Infrastructure Health (Automated)

**Goal:** Verify the Azure infrastructure is alive and the new code is actually running.

### 1.1 Revision Status

Verify all container app revisions are **Running** (not Activating, not ActivationFailed, not Degraded).

```bash
for APP in api webapp marketing workers; do
  REVISION_STATE=$(az containerapp revision list \
    --name "$APP" --resource-group rg-sstudio-prod \
    --query "[?properties.trafficWeight > \`0\`].{name:name, state:properties.runningState, weight:properties.trafficWeight, created:properties.createdTime}" \
    -o json 2>/dev/null)
  echo "=== $APP ==="
  echo "$REVISION_STATE" | jq .
done
```

**Pass condition:** Every app's active revision (trafficWeight > 0) has `runningState: "Running"`.
**Fail examples:** `Activating` (stuck startup), `Failed` (crash loop), `Degraded` (partial failure).

### 1.2 Active Revision Is Latest

Verify the revision receiving traffic (trafficWeight=100) is the **most recently created** revision — not an old one that auto-scaled back up.

```bash
for APP in api webapp; do
  LATEST=$(az containerapp revision list \
    --name "$APP" --resource-group rg-sstudio-prod \
    --query "sort_by(@, &properties.createdTime)[-1].name" -o tsv)
  ACTIVE=$(az containerapp revision list \
    --name "$APP" --resource-group rg-sstudio-prod \
    --query "[?properties.trafficWeight > \`0\`].name | [0]" -o tsv)
  if [ "$LATEST" = "$ACTIVE" ]; then
    echo "PASS: $APP active revision is latest ($ACTIVE)"
  else
    echo "FAIL: $APP active=$ACTIVE but latest=$LATEST — traffic hitting old code!"
  fi
done
```

**Why this matters:** The incident on 2025-07-26 showed login "working" because traffic was hitting an old revision while the new one was crash-looping.

### 1.3 No Crash Loops

Check the last 60 seconds of API container logs for crash indicators.

```bash
az containerapp logs show \
  --name api --resource-group rg-sstudio-prod \
  --tail 100 --type system 2>/dev/null | grep -iE "backoff|crash|oom|killed|exit code [^0]|unhealthy"
```

**Pass condition:** Zero matches.
**Fail condition:** Any match means the container is restarting repeatedly.

### 1.4 Database Connectivity

Verify the API can actually reach the Postgres Flexible Server.

```bash
# Option A: Direct DB query (requires admin creds)
az postgres flexible-server execute \
  --name <flexible-server-name> --resource-group rg-sstudio-prod \
  --admin-user <admin> --admin-password <password> \
  --database-name sentencestudio \
  --querytext "SELECT 1 AS alive;"

# Option B: Indirect — hit an API endpoint that queries the DB (preferred)
# The /api/auth/login endpoint talks to the DB. A 400/401 response means the DB is reachable.
# A 500/503 means DB connection failed.
HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
  -X POST https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"healthcheck@test.invalid","password":"x"}')
# 400 or 401 = DB reachable (auth failed correctly). 500/502/503 = DB unreachable.
```

**Pass condition:** HTTP 400 or 401 from login endpoint (means: API is up, DB is connected, auth logic ran, credentials were bad — that's correct).
**Fail condition:** HTTP 500, 502, 503 (means: API crashed or can't reach DB).

### 1.5 Endpoint HTTP Status Codes

Verify all public endpoints return expected status codes.

| Endpoint | Method | Expected | Meaning |
|----------|--------|----------|---------|
| `https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io/` | GET | 200 (with redirect to /auth/login) | WebApp is alive |
| `https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/api/auth/login` | POST (bad creds) | 400 or 401 | API is alive, DB reachable |
| `https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/api/v1/auth/bootstrap` | GET (no JWT) | 401 | Auth middleware working |
| `https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io/weatherforecast` | GET | 200 | API basic routing works |
| `https://www.sentencestudio.com` | GET | 200 | Marketing site alive |

```bash
check_endpoint() {
  local URL="$1" METHOD="${2:-GET}" EXPECTED="$3" LABEL="$4" BODY="$5"
  if [ "$METHOD" = "POST" ]; then
    ACTUAL=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$URL" \
      -H "Content-Type: application/json" -d "$BODY" --max-time 15)
  else
    ACTUAL=$(curl -s -o /dev/null -w "%{http_code}" -L "$URL" --max-time 15)
  fi
  if echo "$EXPECTED" | grep -q "$ACTUAL"; then
    echo "PASS: $LABEL → HTTP $ACTUAL"
  else
    echo "FAIL: $LABEL → HTTP $ACTUAL (expected $EXPECTED)"
  fi
}
```

### 1.6 EF Core Migrations

Verify migrations applied successfully on startup.

```bash
az containerapp logs show \
  --name api --resource-group rg-sstudio-prod \
  --tail 50 | grep -iE "applying migration|database is up to date|migrat"
```

**Pass condition:** Logs contain migration confirmation or "up to date".

---

## Phase 2: Functional Smoke Test (Automated)

**Goal:** Verify the core user flows work end-to-end, not just that endpoints respond.

### 2.1 Auth Flow — Register or Login

Use a dedicated test account. Do NOT use real user credentials in scripts.

```bash
API_BASE="https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io"

# Try login with test account
LOGIN_RESPONSE=$(curl -s -X POST "$API_BASE/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"deploy-test@sentencestudio.app","password":"DeployTest123!"}')

# Extract JWT
JWT=$(echo "$LOGIN_RESPONSE" | jq -r '.token // empty')
if [ -z "$JWT" ]; then
  echo "WARN: Test account login failed — may need registration or email confirmation"
  echo "Response: $LOGIN_RESPONSE"
else
  echo "PASS: Login succeeded, JWT obtained"
fi
```

### 2.2 Protected Endpoint with JWT

```bash
if [ -n "$JWT" ]; then
  BOOTSTRAP=$(curl -s -w "\n%{http_code}" \
    "$API_BASE/api/v1/auth/bootstrap" \
    -H "Authorization: Bearer $JWT")
  HTTP_CODE=$(echo "$BOOTSTRAP" | tail -1)
  BODY=$(echo "$BOOTSTRAP" | head -n -1)
  if [ "$HTTP_CODE" = "200" ]; then
    echo "PASS: Bootstrap endpoint returned user data"
    echo "$BODY" | jq '{tenantId, userId, displayName, email}'
  else
    echo "FAIL: Bootstrap endpoint returned HTTP $HTTP_CODE"
  fi
fi
```

### 2.3 WebApp Renders

Verify the webapp loads a real page (not a blank page or error screen).

```bash
WEBAPP_URL="https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io"
WEBAPP_BODY=$(curl -sL "$WEBAPP_URL" --max-time 15)

# Check for key indicators that the Blazor app loaded
if echo "$WEBAPP_BODY" | grep -q "_framework/blazor"; then
  echo "PASS: WebApp Blazor framework loaded"
else
  echo "FAIL: WebApp did not return Blazor content"
fi

if echo "$WEBAPP_BODY" | grep -q "SentenceStudio\|Sentence Studio\|login"; then
  echo "PASS: WebApp contains expected content"
else
  echo "FAIL: WebApp content looks wrong — possible blank page or error"
fi
```

### 2.4 WebApp via Playwright (optional, if Playwright MCP is available)

For deeper webapp validation when running from an AI agent context:

1. Navigate to `https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io/`
2. Verify redirect to `/auth/login`
3. Verify login form is visible (email + password fields)
4. Login with test credentials
5. Verify dashboard loads (look for navigation elements, user profile)
6. Navigate to vocabulary page, verify it renders
7. Navigate to quiz page, verify it starts

This is the **"poke around like a human would"** step.

---

## Phase 3: Change-Specific Validation (Semi-Automated)

**Goal:** Verify that the specific change you deployed is actually live in the running system.

### How It Works

Every deploy MUST include a description of what changed. The deployer (human or AI agent) writes a brief changelist and the corresponding validation steps.

### Changelist Template

Before deploying, fill in:

```
DEPLOY CHANGELIST:
- [ ] Change 1: <what changed>
  Validation: <how to verify it's live>
- [ ] Change 2: <what changed>
  Validation: <how to verify it's live>
```

### Common Validation Patterns

| Change Type | Validation |
|-------------|-----------|
| Database migration (e.g., new column) | Query the DB for the new column: `SELECT column_name FROM information_schema.columns WHERE table_name='X'` |
| New API endpoint | `curl` the endpoint, verify expected response shape |
| UI change (webapp) | Load the page in Playwright, screenshot, verify visual change |
| Config change (connection string) | Check container app env vars: `az containerapp show --name api ...` or verify via API behavior |
| Auth change | Login flow, verify JWT claims, test protected endpoints |
| Managed Postgres migration | Verify connection string does NOT contain `db.internal`, DOES contain `postgres.database.azure.com` |

### Example: "Migrated to Managed Postgres"

```bash
# Verify the API's connection string points to Flexible Server, not the container
az containerapp show --name api --resource-group rg-sstudio-prod \
  --query "properties.template.containers[0].env[?name=='ConnectionStrings__sentencestudio'].value" -o tsv \
  | grep -q "postgres.database.azure.com" && echo "PASS: Using managed Postgres" || echo "FAIL: Still using container DB"
```

### Example: "Added mastery scoring endpoint"

```bash
curl -s -o /dev/null -w "%{http_code}" \
  -X POST "$API_BASE/api/v1/vocabulary/test-word-id/status" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{}' | grep -qE "200|400" && echo "PASS" || echo "FAIL"
```

---

## Phase 4: Regression Check (Automated)

**Goal:** Verify that existing functionality wasn't broken by the deploy.

### 4.1 Core API Endpoints

```bash
API_BASE="https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io"

# These should all return expected codes even without auth
check_endpoint "$API_BASE/weatherforecast" GET "200" "Weatherforecast (basic routing)"
check_endpoint "$API_BASE/api/v1/auth/bootstrap" GET "401" "Bootstrap (auth guard)"
check_endpoint "$API_BASE/api/auth/login" POST "400|401" "Login (auth endpoint)" '{"email":"x","password":"x"}'
check_endpoint "$API_BASE/api/auth/register" POST "400" "Register (validation)" '{"email":"bad","password":"x"}'
```

### 4.2 WebApp Core Pages (via Playwright)

If Playwright MCP is available, verify these pages load without errors:

1. `/auth/login` — login form renders
2. `/` or `/dashboard` — dashboard loads after login
3. `/vocabulary` — vocabulary list renders
4. `/quiz` — quiz page loads

### 4.3 Marketing Site

```bash
MARKETING_CODE=$(curl -s -o /dev/null -w "%{http_code}" -L "https://www.sentencestudio.com" --max-time 15)
if [ "$MARKETING_CODE" = "200" ]; then
  echo "PASS: Marketing site responds"
else
  echo "FAIL: Marketing site returned HTTP $MARKETING_CODE"
fi
```

---

## Validation Result Format

After running all checks, produce a summary:

```
╔══════════════════════════════════════════════╗
║           POST-DEPLOY VALIDATION             ║
╠══════════════════════════════════════════════╣
║ Phase 1: Infrastructure Health               ║
║   1.1 Revision status     : PASS / FAIL      ║
║   1.2 Active = latest     : PASS / FAIL      ║
║   1.3 No crash loops      : PASS / FAIL      ║
║   1.4 DB connectivity     : PASS / FAIL      ║
║   1.5 Endpoint status     : PASS / FAIL      ║
║   1.6 EF migrations       : PASS / FAIL      ║
║                                              ║
║ Phase 2: Functional Smoke Test               ║
║   2.1 Auth flow (login)   : PASS / FAIL      ║
║   2.2 Protected endpoint  : PASS / FAIL      ║
║   2.3 WebApp renders      : PASS / FAIL      ║
║   2.4 WebApp Playwright   : PASS / SKIP      ║
║                                              ║
║ Phase 3: Change-Specific                     ║
║   [changelist items]      : PASS / FAIL      ║
║                                              ║
║ Phase 4: Regression Check                    ║
║   4.1 Core API endpoints  : PASS / FAIL      ║
║   4.2 WebApp pages        : PASS / SKIP      ║
║   4.3 Marketing site      : PASS / FAIL      ║
╠══════════════════════════════════════════════╣
║ OVERALL:  PASS / FAIL                        ║
╚══════════════════════════════════════════════╝
```

**OVERALL is PASS only if ALL checks pass (SKIP counts as pass).**

---

## Who Runs This

| Deployer | How |
|----------|-----|
| **Squad (AI agents)** | Run `scripts/post-deploy-validate.sh` automatically after every `azd deploy`. Script exit code 0 = pass, non-zero = fail. Phase 3 items must be specified in the deploy command. |
| **Human (Captain)** | Run `scripts/post-deploy-validate.sh` from terminal. For Phase 3, manually verify the specific change. For Phase 2.4 / 4.2, open the webapp in a browser and click around. |

---

## Escalation

If validation fails:

1. **Do NOT declare the deploy successful.**
2. Check container logs: `az containerapp logs show --name api --resource-group rg-sstudio-prod --tail 200`
3. Check if the old revision is still handling traffic (1.2 above).
4. If the new revision is crash-looping, the ACA platform may auto-route traffic back to the old revision — this is why "login works" does NOT mean "deploy worked."
5. Fix the issue, re-deploy, re-validate. The validation cycle is: deploy → wait 30s → validate → if fail → fix → deploy → wait 30s → validate.

---

## Appendix: API Endpoint Reference

| Path | Method | Auth | Purpose |
|------|--------|------|---------|
| `/api/auth/register` | POST | No | Register new user |
| `/api/auth/login` | POST | No | Login, returns JWT |
| `/api/auth/refresh` | POST | No | Refresh JWT token |
| `/api/auth/confirm-email` | GET | No | Email confirmation |
| `/api/auth/forgot-password` | POST | No | Password reset request |
| `/api/auth/reset-password` | POST | No | Password reset |
| `/api/v1/auth/bootstrap` | GET | JWT | Get current user info |
| `/api/v1/ai/chat` | POST | JWT | AI chat |
| `/api/v1/ai/chat-messages` | POST | JWT | AI multi-message chat |
| `/api/v1/ai/analyze-image` | POST | JWT | Image analysis |
| `/api/v1/speech/synthesize` | POST | JWT | TTS via ElevenLabs |
| `/api/v1/plans/generate` | POST | JWT | Generate learning plan |
| `/api/v1/vocabulary/{wordId}/status` | POST | JWT | Update vocab status |
| `/weatherforecast` | GET | No | Test endpoint |
| `/api/channels/*` | Various | JWT | YouTube channel endpoints |
| `/api/import/*` | Various | JWT | Import endpoints |
| `/api/feedback/*` | POST | JWT | GitHub issue creation |
| `/api/version/*` | GET | No | Version/release notes |
