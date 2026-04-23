
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
