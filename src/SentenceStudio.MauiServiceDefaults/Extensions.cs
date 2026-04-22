using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.RegularExpressions;

namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    public static TBuilder AddMauiServiceDefaults<TBuilder>(this TBuilder builder, string platformName = "Unknown") where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry(platformName);

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default with increased timeouts for AI calls.
            // Default AttemptTimeout is 30s which is too short for long transcript
            // polishing and vocabulary extraction via the API gateway.
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

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, string platformName = "Unknown") where TBuilder : IHostApplicationBuilder
    {
        // Tag every signal with a stable service name so App Insights cloud_RoleName
        // clearly identifies the mobile client (not just "SentenceStudio").
        // Platform is threaded in from the platform head's MauiProgram.cs because
        // `DeviceInfo.Platform` is not guaranteed to be populated while the host
        // builder is still configuring (pre-`MauiApp.Build()`).
        var platform = string.IsNullOrWhiteSpace(platformName) ? "Unknown" : platformName;
        var serviceName = $"SentenceStudio.Mobile.{platform}";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: serviceName));

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
                    // Manual API-call activities from ApiActivityHandler. Needed because
                    // MAUI doesn't run IHostedService, which means the OTel auto-
                    // instrumentation for HttpClient may not start its ActivitySource
                    // listeners. See SentenceStudio.Services.Observability.ApiActivityHandler
                    // for the full rationale + follow-up issue link.
                    .AddSource("SentenceStudio.Mobile.HttpClient")
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
            // GetRequiredService (vs GetService) guarantees construction — if the provider
            // isn't registered we want a hard failure at startup instead of silently-broken
            // telemetry later. MAUI doesn't run IHostedService, so this forced resolution
            // is what actually materialises the providers. See ApiActivityHandler docs for
            // the full story and the tracked framework-level follow-up.
            services.GetRequiredService<MeterProvider>();
            services.GetRequiredService<TracerProvider>();
            services.GetRequiredService<LoggerProvider>();
        }
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Azure Monitor (Application Insights) exporter.
        // - DEBUG builds: compile-time disabled so simulator/dev runs never ship telemetry to prod.
        // - Release builds: enabled iff AzureMonitor:ConnectionString is present in embedded appsettings.
        //   Connection string is write-only (push-only ingestion auth). See
        //   .squad/agents/wash/history.md "Mobile App Insights follow-up" for the security rationale.
#if !DEBUG
        var azureMonitorConnectionString = builder.Configuration["AzureMonitor:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(azureMonitorConnectionString))
        {
            builder.Logging.AddOpenTelemetry(o =>
                o.AddAzureMonitorLogExporter(options => options.ConnectionString = azureMonitorConnectionString));

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                    metrics.AddAzureMonitorMetricExporter(options => options.ConnectionString = azureMonitorConnectionString))
                .WithTracing(tracing =>
                    tracing.AddAzureMonitorTraceExporter(options => options.ConnectionString = azureMonitorConnectionString));
        }
#endif

        return builder;
    }
}
