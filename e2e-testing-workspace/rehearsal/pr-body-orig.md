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

