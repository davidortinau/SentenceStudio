# Skill: Wire Azure Monitor (Application Insights) into an ASP.NET Core / Aspire web tier via OpenTelemetry

**When to use:** Adding Application Insights to an ASP.NET Core web app (or a set of them) that already has (or will have) an OTel pipeline in a `ServiceDefaults`-style shared project. Companion to `maui-azure-monitor` — same resource, distinct `cloud_RoleName`, correlation via W3C `traceparent`.

## Core pattern

**Which package?** There are two choices. Pick based on whether the shared defaults project is *also* referenced by MAUI / client projects:

| Consumer graph | Package | Rationale |
|---|---|---|
| Pure web / worker hosts only | `Azure.Monitor.OpenTelemetry.AspNetCore` (`UseAzureMonitor()`) | One-liner; auto-wires AspNetCore + HttpClient + SqlClient instrumentation. |
| Shared with MAUI / non-web | `Azure.Monitor.OpenTelemetry.Exporter` + manual `AddAzureMonitor{Log,Metric,Trace}Exporter` | `.AspNetCore` transitively requires `Microsoft.AspNetCore.App`, which has no runtime pack for `maccatalyst-*` / `ios-*` / `android-*` RIDs and breaks MAUI builds with `NETSDK1082`. |

If your defaults project is MAUI-adjacent (e.g. it has `<PackageReference Include="Microsoft.Maui.Core" />` or is referenced by an `AppLib` that MAUI heads consume), **always pick the lower-level `Exporter` package**, even in the web-only paths. Then add `OpenTelemetry.Instrumentation.AspNetCore` to each individual web csproj and wire `.AddAspNetCoreInstrumentation()` from its `Program.cs`. The shared defaults stays MAUI-safe.

## Package floor (web-safe / MAUI-safe shared defaults)

```xml
<PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.7.0" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.1" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.15.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.15.0" /><!-- 1.15.1 doesn't exist for this one -->
<PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.12.0" />
```

And **per web csproj** (API, WebApp, Marketing):

```xml
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.15.0" />
```

Bump all OTel peer refs across *every* sibling project (your `AppLib`, etc.) at the same time — Azure Monitor 1.7.0 requires `OpenTelemetry.Extensions.Hosting >= 1.15.1`, and a 1.11.x pin anywhere in the reference graph will surface as `NU1605` downgrade errors in web projects that transitively reference that older project.

## Wiring — shared `ConfigureOpenTelemetry` + per-host `Program.cs`

```csharp
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Resources;

public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, string? cloudRoleName = null)
    where TBuilder : IHostApplicationBuilder
{
    builder.ConfigureOpenTelemetry(cloudRoleName);
    // service discovery, resilient HTTP defaults, etc.
    return builder;
}

public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, string? cloudRoleName = null)
    where TBuilder : IHostApplicationBuilder
{
    // cloud_RoleName — constant literal per caller. AddService(name) populates service.name
    // which Azure Monitor maps to cloud_RoleName. MUST be called on the OpenTelemetryBuilder
    // before WithMetrics / WithTracing so every exporter sees the same resource.
    var roleName = string.IsNullOrWhiteSpace(cloudRoleName) ? builder.Environment.ApplicationName : cloudRoleName;

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(roleName));

    builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage = true; o.IncludeScopes = true; });

    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m.AddHttpClientInstrumentation().AddRuntimeInstrumentation())
        .WithTracing(t => t.AddSource(builder.Environment.ApplicationName).AddHttpClientInstrumentation());

    builder.AddOpenTelemetryExporters();
    return builder;
}

private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
    where TBuilder : IHostApplicationBuilder
{
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        builder.Services.AddOpenTelemetry().UseOtlpExporter();

    // #if !DEBUG so local `aspire run` (Debug) keeps streaming to the Aspire dashboard via OTLP
    // without also dual-exporting to App Insights. Prod containers are Release -> DEBUG undefined
    // -> Azure Monitor activates as long as AzureMonitor:ConnectionString is populated.
#if !DEBUG
    var cs = builder.Configuration["AzureMonitor:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(cs))
    {
        builder.Logging.AddOpenTelemetry(logging =>
            logging.AddAzureMonitorLogExporter(o => o.ConnectionString = cs));
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m.AddAzureMonitorMetricExporter(o => o.ConnectionString = cs))
            .WithTracing(t => t.AddAzureMonitorTraceExporter(o => o.ConnectionString = cs));
    }
#endif
    return builder;
}
```

Per web `Program.cs` (API, WebApp, Marketing):

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("SentenceStudio.Api");

// AspNetCore instrumentation is web-host-local because the package references
// Microsoft.AspNetCore.App (no MAUI runtime pack). Shared defaults stays MAUI-safe.
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddAspNetCoreInstrumentation())
    .WithTracing(t => t.AddAspNetCoreInstrumentation());
```

Worker (`Host.CreateApplicationBuilder`) hosts don't add AspNetCore instrumentation — they have no request pipeline. They just pass their role name:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults("SentenceStudio.Workers");
```

## Client ↔ server correlation

With both sides emitting to the same App Insights resource, distinct `cloud_RoleName` values, and `HttpClient` instrumentation on the client propagating W3C `traceparent`:

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

Rows with non-empty `client_role` = correlated spans. If you get zero after a known mobile → API hit, check (in order): connection string matches on both sides; `cloud_RoleName` literal is non-empty; HttpClient instrumentation registered on client; AspNetCore instrumentation registered on server; wait 2–5 min for ingestion.

## Exception telemetry — no bespoke middleware needed

`OpenTelemetry.Instrumentation.AspNetCore` captures unhandled request exceptions as span events + `Microsoft.AspNetCore.Hosting.Diagnostics` log events automatically. With the log exporter wired, they land as `exceptions` rows in App Insights for free.

What AspNetCore instrumentation does **NOT** catch:
- `BackgroundService` startup failures (before host fully starts).
- Fire-and-forget `Task.Run` without await.
- `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` — timing-dependent.

For those paths, wrap with `try/catch + ILogger.LogCritical(ex, "…")`. The OTel logging provider exports via `AddAzureMonitorLogExporter`.

## Secret placement for the connection string

Write-only ingestion key. Three reasonable homes, pick one per environment:

| Home | When |
|---|---|
| `appsettings.Production.json` in the web csproj | Single shared resource, bundle-embedded acceptable, simplest. |
| `builder.AddParameter("aiConnString")` in Aspire AppHost + `.WithEnvironment("AzureMonitor__ConnectionString", …)` | Multi-env, rotating, or staging-vs-prod differentiation. |
| Env var `APPLICATIONINSIGHTS_CONNECTION_STRING` on the Container App | Matches Azure Monitor SDK's default name; `UseAzureMonitor()` reads it automatically. |

Worst case with a leaked connection string is fake telemetry spam, bounded by the daily ingestion cap. Set a cap (`az monitor app-insights component billing update … --cap 0.5 -s true`) and move on.

## Gotchas

- **Don't double-export via `Aspire.Hosting.AzureMonitor`.** If AppHost uses that integration, it wires `UseAzureMonitor` implicitly into referenced projects. Grep `AppHost.csproj` for `Aspire.Hosting.AzureMonitor` / `Aspire.Hosting.ApplicationInsights` and pick **one** path.
- **`ConfigureResource` before providers.** `AddService(roleName)` must be on the `OpenTelemetryBuilder` before `WithMetrics` / `WithTracing` configuration. If added after, early-registered processors may not see it.
- **Bump all OTel siblings at once.** Version misalignment across `ServiceDefaults`, `AppLib`, `MauiServiceDefaults`, `WebServiceDefaults` manifests as `NU1605` in the downstream-most project — not where the mismatch lives. Grep all four csprojs before merging.
- **Dead `WebServiceDefaults` trap.** In this repo, `SentenceStudio.WebServiceDefaults` exists but nobody references it. Web projects all consume `SentenceStudio.ServiceDefaults` (the MAUI-flavored one, counter-intuitively — it's the load-bearing shared defaults). Don't wire Azure Monitor into the dead one by mistake.
- **`MapDefaultEndpoints` is dev-only.** The scaffolded `app.MapHealthChecks("/health")` is gated to `IsDevelopment()`. Enabling for Container Apps liveness probes is a separate decision.
