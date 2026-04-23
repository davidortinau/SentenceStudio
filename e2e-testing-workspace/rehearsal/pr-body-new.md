# Server-side App Insights: close mobile↔API correlation loop

Companion to PR #165 (the mobile-side slice  that shipped `Azure.Monitor.OpenTelemetry.Exporter` into `SentenceStudio.MauiServiceDefaults`). This PR wires the same App Insights resource (`sstudio-mobile-ai`) into the server tier via `SentenceStudio.ServiceDefaults`, giving us joined `requests` / `dependencies` rows keyed by `operation_Id`.

Agent: **Wash** (Backend Dev). Draft until the deploy + correlation proof below is filled in.

## Cap raise

Daily ingestion cap on `sstudio-mobile-ai` raised from **0.5 GB → 2 GB** before deploy so combined mobile + 4 server emitters (API, WebApp, Workers, Marketing) don't get throttled in the first day.

```bash
az monitor app-insights component billing update \
  --app sstudio-mobile-ai --resource-group rg-sstudio-prod \
  --cap 2 --stop false
```

Result (from `az monitor app-insights component billing show …`):

```json
// BEFORE
{
  "currentBillingFeatures": ["Basic"],
  "dataVolumeCap": {
    "cap": 0.5,
    "maxHistoryCap": 1000.0,
    "resetTime": 0,
    "stopSendNotificationWhenHitCap": true,
    "stopSendNotificationWhenHitThreshold": false,
    "warningThreshold": 90
  }
}

// AFTER
{
  "currentBillingFeatures": ["Basic"],
  "dataVolumeCap": {
    "cap": 2.0,
    "maxHistoryCap": 1000.0,
    "resetTime": 0,
    "stopSendNotificationWhenHitCap": false,
    "stopSendNotificationWhenHitThreshold": false,
    "warningThreshold": 90
  }
}
```

> CLI quirk: `--stop` / `-s` only exposes `stopSendNotificationWhenHitCap`. There is no `--stop-sending-notification-when-hitting-threshold` flag despite what some docs claim — the CLI rejects it. Notifications at 90% threshold remain enabled.

## Exception handler

Added a global `app.UseExceptionHandler(…)` as the first middleware in `src/SentenceStudio.Api/Program.cs` (just after `builder.Build()`, lines 276–302) that logs unhandled exceptions via a named `UnhandledException` `ILogger` and returns `application/problem+json` 500. This is required on top of `AddAspNetCoreInstrumentation` — that instrumentation tags the request span with exception events but does NOT produce rows in App Insights' `exceptions` table (those come only from `ILogger` records carrying an `Exception`, shipped through the OTel log exporter).

Smoke-validated locally via a temporary `/__debug/boom` endpoint (removed before commit): HTTP 500 + problem+json body, `fail: UnhandledException[0]` log line with full stack, process kept running for further requests.

## What's in this PR

| File | Change |
|---|---|
| `src/SentenceStudio.ServiceDefaults/SentenceStudio.ServiceDefaults.csproj` | OTel → 1.15.x; added `Azure.Monitor.OpenTelemetry.Exporter 1.7.0`. |
| `src/SentenceStudio.ServiceDefaults/Extensions.cs` | `AddServiceDefaults(..., cloudRoleName)`; `ConfigureResource(AddService(roleName))`; `#if !DEBUG` three-exporter Azure Monitor block gated on `AzureMonitor:ConnectionString`. |
| `src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj` | OTel 1.11.x → 1.15.x to clear NU1605 downgrades surfaced by the ServiceDefaults bump. |
| `src/SentenceStudio.Api/SentenceStudio.Api.csproj` | Added `OpenTelemetry.Instrumentation.AspNetCore 1.15.0` **locally** (kept out of shared defaults — see MAUI-safety note below). |
| `src/SentenceStudio.Api/Program.cs` | `AddServiceDefaults("SentenceStudio.Api")`; local `.WithMetrics/.WithTracing` with `AddAspNetCoreInstrumentation()`; **global `UseExceptionHandler` → `ILogger.LogError`** (new; pre-deploy review fix). |
| `src/SentenceStudio.Api/appsettings.Production.json` | Added `AzureMonitor:ConnectionString` (write-only ingestion key, same as mobile's — intentional reuse). |
| `src/SentenceStudio.WebApp/Program.cs`, `Workers/Program.cs`, `Marketing/Program.cs` | Each now passes its own `cloud_RoleName` literal. No connection string shipped in their `appsettings.Production.json` yet → they stay OTLP-only until Captain opts them in. |
| `.squad/skills/aspnetcore-azure-monitor/SKILL.md` | Sibling to `maui-azure-monitor/SKILL.md`. Captures the MAUI-safe server pattern + the exception-handler recipe + the cap-raise az CLI recipe. |
| `.squad/agents/wash/history.md` | Appended learnings sections for 2026-04-22 (server slice + review fixes). |

## Locked-decisions adherence

- **ONE App Insights resource** (`sstudio-mobile-ai`, workspace-backed by `law-3ovvqiybthkb6`): ✅ reused verbatim, same connection string as mobile.
- **Constant literal `cloud_RoleName`, no runtime detection:** ✅ `"SentenceStudio.Api"`, `"SentenceStudio.WebApp"`, `"SentenceStudio.Workers"`, `"SentenceStudio.Marketing"` — all passed from their respective `Program.cs`.
- **Local-dev null-out, OTLP → Aspire dashboard preserved:** ✅ Azure Monitor wiring is `#if !DEBUG`. Container builds are Release so it activates in prod; `aspire run` is Debug so it stays OTLP-only. No double-export.
- **No `Aspire.Hosting.AzureMonitor` integration in AppHost:** ✅ verified absent. The manual wiring in ServiceDefaults is the only path.

## MAUI-safety pivot (deviation from task brief)

The task brief suggested `Azure.Monitor.OpenTelemetry.AspNetCore 1.4.0` + `UseAzureMonitor()`. That package transitively pulls `OpenTelemetry.Instrumentation.AspNetCore`, which declares `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. There is **no `Microsoft.AspNetCore.App` runtime pack for `maccatalyst-arm64` / `ios-arm64` / `android-*` RIDs**, so putting it in `ServiceDefaults` broke every MAUI head with `NETSDK1082`.

`ServiceDefaults` is consumed by web hosts *and* (transitively, via `AppLib`) by every MAUI head. So the `.AspNetCore` variant of Azure Monitor simply can't live in the shared project.

**Resolution:** swapped to the lower-level `Azure.Monitor.OpenTelemetry.Exporter 1.7.0` — exactly what `MauiServiceDefaults` already uses client-side — with the three `AddAzureMonitor{Log,Metric,Trace}Exporter` calls. Added `OpenTelemetry.Instrumentation.AspNetCore` **only** to the API's csproj, and wired `.AddAspNetCoreInstrumentation()` from `Program.cs`. Net observability fidelity matches `UseAzureMonitor()`; MAUI stays buildable. The MAUI-safety note was already documented in the `maui-azure-monitor` skill; the sibling `aspnetcore-azure-monitor` skill in this PR captures the server flip-side.

## Build proof

All zero-error:
- `dotnet build src/SentenceStudio.Api -f net10.0 -c Debug` ✅
- `dotnet build src/SentenceStudio.Api -f net10.0 -c Release` ✅
- `dotnet build src/SentenceStudio.WebApp -c Release` ✅
- `dotnet build src/SentenceStudio.Workers -c Release` ✅
- `dotnet build src/SentenceStudio.Marketing -c Release` ✅
- `dotnet build src/SentenceStudio.MacCatalyst -f net10.0-maccatalyst -c Debug` ✅ (the MAUI-safety proof)

## Deploy + validation — **TO BE FILLED IN BEFORE MARKING READY**

_Captain to confirm VPN off, then:_

```bash
./scripts/pre-deploy-check.sh         # resource locks, DB, volume mount, storage, file share
azd deploy                            # full-stack; azure.yaml has a single `app` mapping AppHost
./scripts/post-deploy-validate.sh     # infra + smoke
```

Then generate mobile→API traffic (any authenticated call from Mac Catalyst on DX24 or sim in Release), wait 2–5 min for ingestion, and paste results of these three KQL queries into a follow-up comment:

**1. Server requests are flowing with the right role name**
```kusto
requests
| where timestamp > ago(15m)
| where cloud_RoleName == "SentenceStudio.Api"
| summarize count() by name, resultCode
| order by count_ desc
```

**2. Mobile → API correlation (the money shot)**
```kusto
requests
| where timestamp > ago(15m)
| where cloud_RoleName == "SentenceStudio.Api"
| project timestamp, name, operation_Id, operation_ParentId, success, resultCode
| join kind=leftouter (
    dependencies
    | where cloud_RoleName startswith "SentenceStudio.Mobile"
    | project depTimestamp=timestamp, depName=name, operation_Id, client_role=cloud_RoleName
  ) on operation_Id
| where isnotempty(client_role)
```
Expect ≥1 row with `client_role = SentenceStudio.Mobile.MacCatalyst` (or iOS).

**3. Server-side exceptions land with the right role name**
Trigger a malformed payload against any API endpoint, then:
```kusto
exceptions
| where timestamp > ago(15m)
| where cloud_RoleName == "SentenceStudio.Api"
| project timestamp, type, outerMessage, operation_Id, cloud_RoleInstance
```

## Out of scope (follow-ups noted in `.squad/decisions/inbox/wash-server-appinsights-shipped.md`)

- Custom sampling / telemetry processors.
- Alerts + dashboards (5xx spike, OpenAI failure rate, mobile↔API latency).
- ~~Global `UseExceptionHandler` + `AddProblemDetails` middleware~~ — **landed in this PR as pre-deploy review fix.**
- `BackgroundService` startup-failure wrapping in Workers (silent before OTel sees them).
- `/health` endpoint for ACA liveness probes — currently gated to `IsDevelopment()`.
- `SentenceStudio.WebServiceDefaults` is dead code (nobody references it) — delete or migrate web projects to it in a separate PR.
- Rolling out the connection string to WebApp/Workers/Marketing `appsettings.Production.json` — they'll export to App Insights the moment we do.
- Managed Identity instead of write-only connection string — 🟠 follow-up only; out of scope for this PR.

## Known unrelated breakage

`ci.yml` has been red on `main` since ~Apr 17 (`wasm-tools` workload missing for `net10.0-ios` on Ubuntu). Pre-existing; not in scope here.


## Dress rehearsal (Release-build, local — 2026-04-22)

Before risking `azd deploy`, the API was built **Release** and run standalone against a local Docker Postgres (`sstudio-pg-rehearsal`, `postgres:16`, port 5433) with `ASPNETCORE_ENVIRONMENT=Production` and the real `AzureMonitor:ConnectionString` from `appsettings.Production.json`. This activates the `#if !DEBUG` branch of `SentenceStudio.ServiceDefaults.AddOpenTelemetryExporters` → live telemetry to `sstudio-mobile-ai`. Zero blast radius to the deployed container.

Smoke tests (API on `https://localhost:7801`):
- `POST /api/auth/login` with bad creds → **401** ✅
- `GET /__debug/boom` (temp endpoint, removed before commit) → **500** + `application/problem+json` body ✅
- `POST /api/auth/login` with an injected W3C `traceparent: 00-5c4324bba96c15b5da00f712ac863982-d96513170a11dd97-01` → **401** ✅

After ~4 min ingestion wait, these KQL queries against `sstudio-mobile-ai` (App ID `74e94530-…`) all returned non-empty:

### Query A — `exceptions` table (the Fix 🟡 proof)

```kusto
exceptions
| where timestamp > ago(15m)
| where cloud_RoleName == "SentenceStudio.Api"
| where outerMessage has "Dress rehearsal"
| project timestamp, type, outerMessage, cloud_RoleName, operation_Id
| order by timestamp desc
| take 5
```

4 rows returned (2 distinct operation_Ids × 2 log entries each from the ExceptionHandler + UnhandledException loggers):

| timestamp | type | outerMessage | cloud_RoleName | operation_Id |
|---|---|---|---|---|
| 2026-04-22T01:44:30.66344Z | System.InvalidOperationException | Dress rehearsal: server-side AppInsights exception capture | SentenceStudio.Api | f024e18789cefa6595e8d17a3addf15b |
| 2026-04-22T01:44:30.663304Z | System.InvalidOperationException | Dress rehearsal: server-side AppInsights exception capture | SentenceStudio.Api | f024e18789cefa6595e8d17a3addf15b |
| 2026-04-22T01:44:30.627562Z | System.InvalidOperationException | Dress rehearsal: server-side AppInsights exception capture | SentenceStudio.Api | 41700dd3f0449dce514423173427f4b9 |
| 2026-04-22T01:44:30.627123Z | System.InvalidOperationException | Dress rehearsal: server-side AppInsights exception capture | SentenceStudio.Api | 41700dd3f0449dce514423173427f4b9 |

Proves the `UseExceptionHandler` → `ILogger.LogError` → OTel log exporter → App Insights `exceptions` table chain works end-to-end. **This is the check that was green-field in the review fix (PR commit `4ff69c7`).**

### Query B — `requests` table (instrumentation + role name)

```kusto
requests
| where timestamp > ago(15m)
| where cloud_RoleName == "SentenceStudio.Api"
| project timestamp, name, resultCode, duration, operation_Id
| order by timestamp desc
| take 10
```

4 rows:

| timestamp | name | resultCode | duration (ms) | operation_Id |
|---|---|---|---|---|
| 2026-04-22T01:44:30.834154Z | POST /api/auth/login | 401 | 5.528 | 5c4324bba96c15b5da00f712ac863982 |
| 2026-04-22T01:44:30.661627Z | GET /__debug/boom | 500 | 1.85 | f024e18789cefa6595e8d17a3addf15b |
| 2026-04-22T01:44:30.612819Z | GET /__debug/boom | 500 | 15.023 | 41700dd3f0449dce514423173427f4b9 |
| 2026-04-22T01:44:30.371703Z | POST /api/auth/login | 401 | 207.482 | 87dc748e91e149db7bab784a52ef54cf |

Proves `AddAspNetCoreInstrumentation` + `AddAzureMonitorTraceExporter` pipeline ships `requests` rows with the correct `cloud_RoleName = "SentenceStudio.Api"`.

### Query C — W3C traceparent propagation (the correlation proof)

```kusto
union requests, dependencies
| where timestamp > ago(15m)
| where operation_Id == "5c4324bba96c15b5da00f712ac863982"
| project timestamp, itemType, name, cloud_RoleName, operation_Id, operation_ParentId
```

2 rows — the server adopted the injected trace id AND the injected span id as parent:

| timestamp | itemType | name | cloud_RoleName | operation_Id | operation_ParentId |
|---|---|---|---|---|---|
| 2026-04-22T01:44:30.834154Z | request | POST /api/auth/login | SentenceStudio.Api | 5c4324bba96c15b5da00f712ac863982 | d96513170a11dd97 |
| 2026-04-22T01:44:30.838081Z | dependency | postgresql | SentenceStudio.Api | 5c4324bba96c15b5da00f712ac863982 | ab809153d6e3145d |

`operation_ParentId = d96513170a11dd97` exactly matches the span id we sent in the `traceparent` header. **This is the proof that when Mac Catalyst (or any mobile head running `MauiServiceDefaults` with HttpClient instrumentation) calls the deployed API, the server span will inherit the mobile-originated trace id automatically** — Query 2 of the pre-deploy proof block (mobile→API correlation) will light up the moment real mobile traffic hits the deployed container. And the Postgres dependency row in the same operation shows server-internal spans also hang off the correct trace, so mobile→API→DB will all chain under one operation_Id.

### What's still unproven

- Container Apps startup path (only `azd deploy` proves it).
- Production DNS (`api.livelyforest-b32e7d63.centralus.azurecontainerapps.io`).
- DX24 → real API correlation under production load.

These are the residuals `azd deploy` is expected to cover. The dress rehearsal de-risks the code itself; leaving this PR **draft** for Captain to flip ready-for-review and run `azd deploy`.
