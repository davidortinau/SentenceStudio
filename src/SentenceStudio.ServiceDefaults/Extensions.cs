using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// MAUI-free server defaults: OpenTelemetry, service discovery and HTTP resilience.
// Web hosts add ASP.NET Core instrumentation and own their health checks/endpoints.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    /// <param name="azureMonitorCredential">
    /// Builds the Entra credential for the Azure Monitor exporters when
    /// <c>AzureMonitor:Authentication</c> is <c>ManagedIdentity</c> (#168). Server hosts pass
    /// <c>() =&gt; new DefaultAzureCredential()</c>; this library stays free of Azure.Identity,
    /// since the client apps share it.
    /// </param>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, string? cloudRoleName = null,
        Func<TokenCredential>? azureMonitorCredential = null) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry(cloudRoleName, azureMonitorCredential);

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

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, string? cloudRoleName = null,
        Func<TokenCredential>? azureMonitorCredential = null) where TBuilder : IHostApplicationBuilder
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
                metrics.AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Learning Coach metrics. Name must match SentenceStudio.Api's
                    // CoachTelemetry.MeterName; CoachTelemetryNameContractTests pins the literal.
                    // Registered here rather than in the API so hosts get it from one place; the
                    // meter simply never emits on hosts that do not run the coach.
                    .AddMeter(CoachTelemetryNames.MeterName);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    // Learning Coach spans. Name must match SentenceStudio.Api's
                    // CoachTelemetry.ActivitySourceName; CoachTelemetryNameContractTests pins it.
                    .AddSource(CoachTelemetryNames.ActivitySourceName)
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters(azureMonitorCredential);

        return builder;
    }

    /// <summary>The <c>AzureMonitor:Authentication</c> value that sends Entra tokens (#168).</summary>
    public const string AzureMonitorManagedIdentity = "ManagedIdentity";

    /// <summary>
    /// The credential the Azure Monitor exporters use: null (instrumentation key in the connection
    /// string) unless <c>AzureMonitor:Authentication</c> is <c>ManagedIdentity</c>. Then the
    /// host must supply one; a host that cannot is a configuration error, not a silent fallback.
    /// </summary>
    public static TokenCredential? ResolveAzureMonitorCredential(
        IConfiguration configuration, Func<TokenCredential>? azureMonitorCredential)
    {
        var mode = configuration["AzureMonitor:Authentication"];
        if (string.IsNullOrWhiteSpace(mode))
            return null;
        if (!string.Equals(mode, AzureMonitorManagedIdentity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"AzureMonitor:Authentication '{mode}' is not supported; use '{AzureMonitorManagedIdentity}' or leave it unset.");
        return azureMonitorCredential?.Invoke() ?? throw new InvalidOperationException(
            "AzureMonitor:Authentication is ManagedIdentity, but this host supplies no credential.");
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder,
        Func<TokenCredential>? azureMonitorCredential) where TBuilder : IHostApplicationBuilder
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
        // Keep the existing separate log, metric and trace exporters for server hosts.
#if !DEBUG
        var azureMonitorConnectionString = builder.Configuration["AzureMonitor:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(azureMonitorConnectionString))
        {
            // With a credential the exporters send Entra tokens, so the resource can turn off
            // instrumentation-key ingestion (DisableLocalAuth). See docs/deploy-runbook.md.
            var credential = ResolveAzureMonitorCredential(builder.Configuration, azureMonitorCredential);
            void Configure(AzureMonitorExporterOptions o)
            {
                o.ConnectionString = azureMonitorConnectionString;
                if (credential is not null)
                    o.Credential = credential;
            }

            builder.Logging.AddOpenTelemetry(logging => logging.AddAzureMonitorLogExporter(Configure));

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(Configure))
                .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(Configure));
        }
#endif

        return builder;
    }
}

/// <summary>
/// Well-known OpenTelemetry source and meter names for the Learning Coach.
/// </summary>
/// <remarks>
/// These live in ServiceDefaults because it is independent of the API and cannot reference
/// <c>SentenceStudio.Api</c>, where the coach itself lives. The API's <c>CoachTelemetry</c> declares
/// the same names, and a unit test asserts the two agree — if they ever drift, coach spans and
/// metrics would be created but never exported.
/// </remarks>
public static class CoachTelemetryNames
{
    /// <summary>The coach <c>ActivitySource</c> name.</summary>
    public const string ActivitySourceName = "SentenceStudio.Coach";

    /// <summary>The coach <c>Meter</c> name.</summary>
    public const string MeterName = "SentenceStudio.Coach";
}
