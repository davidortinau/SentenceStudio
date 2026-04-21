# Skill: Wire Azure Monitor (Application Insights) into a .NET MAUI app via OpenTelemetry

**When to use:** Adding Application Insights to a MAUI app that already has (or will have) an OTel pipeline in a `ServiceDefaults`-style shared project. Works for Mac Catalyst, iOS, Android, macOS, and Windows heads that share a Maui service-defaults module.

## Core pattern

Use `Azure.Monitor.OpenTelemetry.Exporter` — **not** `Microsoft.ApplicationInsights.*` (legacy, does not correlate with server-side OTel). Wire the three exporters directly on the existing logging/metrics/tracing builders; don't reach for `UseAzureMonitor(...)` — that extension is in `Azure.Monitor.OpenTelemetry.AspNetCore` and drags ASP.NET Core dependencies you don't want on mobile.

## Package floor

Azure Monitor 1.7.0 requires `OpenTelemetry.Extensions.Hosting >= 1.15.1`. If your service-defaults project pins older OTel versions (1.9.x / 1.11.x), you'll hit `NU1605` on restore. Bump the whole OTel set together:

```xml
<PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.7.0" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.1" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.15.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.15.0" /><!-- 1.15.1 doesn't exist for this one -->
<PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.12.0" />
```

## Wiring — in `ConfigureOpenTelemetry`

```csharp
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Resources;

public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, string platformName = "Unknown") where TBuilder : IHostApplicationBuilder
{
    // Stable service name → App Insights cloud_RoleName.
    // IMPORTANT: do NOT read `DeviceInfo.Platform` here. This method runs while the host
    // builder is still configuring (pre-`MauiApp.Build()`); MAUI Essentials aren't
    // guaranteed to be initialized, and you'll get "Unknown" or a throw.
    // Thread the platform in from each platform head's `MauiProgram.cs` where the per-TFM
    // symbols are unambiguous. Add an overload `AddMauiServiceDefaults(builder, platformName)`
    // and call `AddMauiServiceDefaults("MacCatalyst")` / `"iOS"` / `"Android"` from each head.
    var platform = string.IsNullOrWhiteSpace(platformName) ? "Unknown" : platformName;
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService($"MyApp.Mobile.{platform}"));

    builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage = true; o.IncludeScopes = true; });
    builder.Services.AddOpenTelemetry()
        .WithMetrics(m => m.AddHttpClientInstrumentation().AddRuntimeInstrumentation())
        .WithTracing(t => t.AddSource(builder.Environment.ApplicationName).AddHttpClientInstrumentation());

    builder.AddOpenTelemetryExporters();
    return builder;
}

private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
{
    // Dev OTLP (Aspire dashboard) — unchanged.
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        builder.Services.AddOpenTelemetry().UseOtlpExporter();

    // Azure Monitor: compile-time gate DEBUG out so simulator runs never ship telemetry.
#if !DEBUG
    var cs = builder.Configuration["AzureMonitor:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(cs))
    {
        builder.Logging.AddOpenTelemetry(o =>
            o.AddAzureMonitorLogExporter(opt => opt.ConnectionString = cs));
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m.AddAzureMonitorMetricExporter(opt => opt.ConnectionString = cs))
            .WithTracing(t => t.AddAzureMonitorTraceExporter(opt => opt.ConnectionString = cs));
    }
#endif
    return builder;
}
```

## Unhandled-exception subscriber (required — without it crashes die silently)

Subscribe from the **app-level builder**, not the service-defaults project (which usually can't reference the `MauiExceptions` helper without a dependency inversion). Resolve the OTel providers from `app.Services`, parallel-flush with a bounded deadline, and gate subscription via `Interlocked` so double-init paths (hot reload, re-entrancy) don't double-wire:

```csharp
// Static gate — Interlocked, not a plain bool, so concurrent init can't race.
private static int _unhandledExceptionWired;

// ... inside InitializeApp(app):
if (Interlocked.Exchange(ref _unhandledExceptionWired, 1) == 0)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MyApp.UnhandledException");
    var logP = app.Services.GetService<LoggerProvider>();
    var traceP = app.Services.GetService<TracerProvider>();
    var meterP = app.Services.GetService<MeterProvider>();

    MauiExceptions.UnhandledException += (sender, args) =>
    {
        try
        {
            logger.LogCritical(args.ExceptionObject as Exception,
                "Unhandled exception (isTerminating={IsTerminating})", args.IsTerminating);

            // Parallel-bounded flush: each provider gets its own 2500ms ceiling,
            // the outer WaitAll caps total wall time at 3000ms. Serial 3s+3s+3s
            // risked a 9s total, exceeding the ~5–10s iOS watchdog on crash paths.
            var flushTasks = new[]
            {
                Task.Run(() => { try { logP?.ForceFlush(2500); }   catch { } }),
                Task.Run(() => { try { traceP?.ForceFlush(2500); } catch { } }),
                Task.Run(() => { try { meterP?.ForceFlush(2500); } catch { } }),
            };
            try { Task.WaitAll(flushTasks, TimeSpan.FromMilliseconds(3000)); } catch { }
        }
        catch { /* never throw from last-chance handler */ }
    };
}
```

Three principles at play here:
1. **Parallel, not serial.** Flushes run concurrently and share a wall-clock budget.
2. **Double-bounded.** Per-provider timeout *and* outer `WaitAll` timeout, so one stuck provider can't steal the whole budget.
3. **Swallow everything.** An exception in your last-chance handler is worse than missed telemetry.

## Forcing an unhandled exception to validate the pipe

- **Don't use `Task.Run(throw)`** — becomes `UnobservedTaskException` only on GC, unreliable timing.
- **Don't use `new Timer(_ => throw)`** — the Timer can be GC'd before firing.
- **Use `new Thread(() => { Thread.Sleep(x); throw …; }).Start()`** — fires `AppDomain.UnhandledException` reliably on iOS/MacCatalyst, which `MauiExceptions` owns.

Gate behind an env var so it's not shipping:

```csharp
if (Environment.GetEnvironmentVariable("APP_CRASH_TEST") == "1")
{
    var t = new Thread(() => { Thread.Sleep(10_000); throw new InvalidOperationException("pipeline validation"); })
            { IsBackground = true, Name = "CrashValidation" };
    t.Start();
}
```

Launch: `open -n --env APP_CRASH_TEST=1 ./MyApp.app` on Mac Catalyst — wait 20s, process should die with SIGABRT and `Unhandled managed exception: ...` in the log.

> **Do NOT invoke the binary directly** (`./MyApp.app/Contents/MacOS/MyApp`). Mac Catalyst aborts in `load_aot_module` when launched outside LaunchServices. Use `open` with `--env` for env-var propagation.

> **Stale `obj/Release` caveat.** Incremental Release builds after OTel package bumps can produce bundles that fail AOT load on `Azure.Core`. Clean `src/*/obj/Release` + rebuild if a fresh Release bundle crashes before reaching managed `Main`.

## Validation KQL (wait 2–5 min for ingestion)

```kusto
exceptions
| where timestamp > ago(15m)
| where cloud_RoleName startswith "MyApp.Mobile"
| project timestamp, type, outerMessage, cloud_RoleName, operation_Id
| order by timestamp desc
```

## Gotchas

- **`cloud_RoleName` detection.** `#if MACCATALYST/IOS/ANDROID` in a plain-`net10.0` `<UseMaui>true</UseMaui>` project does not fire — those symbols are only defined for per-platform TFMs. `DeviceInfo.Platform` at configure-time is also wrong (MAUI Essentials aren't guaranteed to be ready pre-`MauiApp.Build()`). Thread the platform string in from each head's `MauiProgram.cs` — that's the only reliable path.
- **Connection string is write-only.** Safe to embed in the client bundle. Put a daily ingestion cap on the resource (0.5 GB is a reasonable mobile starting point) to bound the blast radius. Do NOT invent a "fetch the key from the API at startup" flow — it's strictly worse (chicken-and-egg when the API is down).
- **Operation correlation is orphan until the server ships.** W3C `traceparent` is propagated via `AddHttpClientInstrumentation` automatically, but the server has to also emit to App Insights for spans to join. Until then, client spans have no parent — expected, not a bug.
- **`ForceFlush` is best-effort.** 3s budget usually beats SIGABRT. The Azure Monitor exporter has a local file cache that retries on next launch, so transient loss on a hard crash is usually recovered. Still, don't rely on the flush for audit-level telemetry — use structured logging from non-crashing paths.
- **Caught exceptions reach App Insights for free.** Any `ILogger.LogError(ex, ...)` or `LogCritical(ex, ...)` call flows through the OTel logging provider → Azure Monitor log exporter. This is a bonus, but also means bugs that log-and-swallow will now show up as `exceptions` records.

## Create the Azure resource

```bash
az monitor app-insights component create \
  --app myapp-mobile-ai \
  --location <region> \
  --kind other \
  --resource-group <rg> \
  --workspace <law-resource-id> \
  --query "connectionString" -o tsv

az monitor app-insights component billing update \
  --app myapp-mobile-ai -g <rg> --cap 0.5 -s true
```

`--workspace` is required (classic App Insights is deprecated). Use an existing Log Analytics workspace to consolidate queries with server-side telemetry.
