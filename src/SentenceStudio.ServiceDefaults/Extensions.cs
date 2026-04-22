using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.RegularExpressions;

namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project is intentionally **MAUI-safe** — it does not pull `OpenTelemetry.Instrumentation.AspNetCore`
// or `Azure.Monitor.OpenTelemetry.AspNetCore` because those reference `Microsoft.AspNetCore.App` which
// has no runtime pack for `maccatalyst-*` / `ios-*` / `android-*` RIDs and fails MAUI builds.
// Web hosts that need ASP.NET Core instrumentation should add it locally in their Program.cs.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, string? cloudRoleName = null) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry(cloudRoleName);

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default with increased timeouts for AI calls
            http.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(300);
            });

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Transient<IMauiInitializeService, OpenTelemetryInitializer>(_ => new OpenTelemetryInitializer()));

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, string? cloudRoleName = null) where TBuilder : IHostApplicationBuilder
    {
        // cloud_RoleName in App Insights — constant literal per consumer (no runtime detection).
        // Falls back to ApplicationName if the caller didn't pass one; prod callers MUST pass an
        // explicit value so client ↔ server correlation is visually distinct.
        var roleName = string.IsNullOrWhiteSpace(cloudRoleName) ? builder.Environment.ApplicationName : cloudRoleName;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(roleName));

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                // Uncomment the following line to enable reporting metrics coming from the .NET MAUI SDK, this might cause a lot of added telemetry
                //metrics.AddMeter("Microsoft.Maui");

                metrics.AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                // Uncomment the following line to enable reporting tracing coming from the .NET MAUI SDK, this might cause a lot of added telemetry
                //tracing.AddSource("Microsoft.Maui");

                tracing.AddSource(builder.Environment.ApplicationName)
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private class OpenTelemetryInitializer : IMauiInitializeService
    {
        public void Initialize(IServiceProvider services)
        {
            services.GetService<MeterProvider>();
            services.GetService<TracerProvider>();
            services.GetService<LoggerProvider>();
        }
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Azure Monitor (Application Insights) — Release-only so local `aspire run` (Debug) keeps
        // streaming to the Aspire dashboard via OTLP without also dual-exporting to App Insights.
        // Prod containers are built Release, so DEBUG is undefined → Azure Monitor activates as
        // long as AzureMonitor:ConnectionString is populated (appsettings.Production.json).
        //
        // Wire the three exporters directly (log / metric / trace) rather than `UseAzureMonitor`
        // from `Azure.Monitor.OpenTelemetry.AspNetCore` — that variant transitively drags
        // `Microsoft.AspNetCore.App` which has no runtime pack for MAUI RIDs and breaks mobile builds.
#if !DEBUG
        var azureMonitorConnectionString = builder.Configuration["AzureMonitor:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(azureMonitorConnectionString))
        {
            builder.Logging.AddOpenTelemetry(logging =>
                logging.AddAzureMonitorLogExporter(o => o.ConnectionString = azureMonitorConnectionString));

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = azureMonitorConnectionString))
                .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = azureMonitorConnectionString));
        }
#endif

        return builder;
    }
}
