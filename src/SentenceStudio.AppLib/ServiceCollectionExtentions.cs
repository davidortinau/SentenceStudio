using CoreSync;
using CoreSync.Http.Client;
using CoreSync.Sqlite;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Observability;
using SentenceStudio.Shared.Models;


namespace SentenceStudio;

public static class ServiceCollectionExtentions
{
    public static void AddSyncServices(this IServiceCollection services, string databasePath, Uri serverUri)
    {
        services.AddSingleton<ISyncProvider>(serviceProvider =>
        {
            var connectionString = $"Data Source={databasePath}";
            var configurationBuilder =
                new SqliteSyncConfigurationBuilder(connectionString)
                    .ConfigureSyncTables();

            return new SqliteSyncProvider(configurationBuilder.Build(), ProviderMode.Local, new SyncLogger(serviceProvider.GetRequiredService<ILogger<SyncLogger>>()));
        });

        services.TryAddApiActivityHandler();

        services.AddHttpClient("HttpClientToServer", httpClient =>
        {
            httpClient.BaseAddress = serverUri;
            httpClient.Timeout = TimeSpan.FromMinutes(10);
        })
        // ApiActivityHandler goes FIRST so the Activity wraps the full request including
        // auth token attachment — gives us accurate latency and a single span per call.
        // With the Activity current, HttpClient's DiagnosticsHandler auto-injects traceparent.
        .AddHttpMessageHandler<ApiActivityHandler>()
        .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();

        services.AddCoreSyncHttpClient(options =>
        {
            options.HttpClientName = "HttpClientToServer";
            //options.UseBinaryFormat = true;
        });
    }

    public static IServiceCollection AddAuthServices(this IServiceCollection services, IConfiguration configuration, Uri? apiBaseUri = null)
    {
        services.AddSingleton<IAuthService, IdentityAuthService>();
        services.AddAuthorizationCore();
        services.AddScoped<AuthenticationStateProvider, MauiAuthenticationStateProvider>();

        services.TryAddApiActivityHandler();

        // Register a named HttpClient for auth endpoints (login, register, refresh).
        // Uses the same API base URL as other clients but without the auth handler
        // to avoid a circular dependency (auth client cannot require auth).
        // The URI (https+http://api) is resolved by Aspire service discovery.
        if (apiBaseUri is not null)
        {
            services.AddHttpClient("AuthClient", client =>
            {
                client.BaseAddress = apiBaseUri;
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .AddHttpMessageHandler<ApiActivityHandler>();
        }

        services.AddTransient<AuthenticatedHttpMessageHandler>();
        return services;
    }

    public static void AddApiClients(this IServiceCollection services, Uri baseUri)
    {
        services.TryAddApiActivityHandler();

        // DelegatingHandlers consumed by HttpClientFactory must be transient.
        services.TryAddTransient<PlanTimeZoneHeaderHandler>();

        services.AddHttpClient<IAiApiClient, AiApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient<ISpeechApiClient, SpeechApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient<IPlansApiClient, PlansApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()
            // Plan generation is keyed to the learner's local date, so it needs the same
            // X-Timezone contract the coach does.
            .AddHttpMessageHandler<PlanTimeZoneHeaderHandler>();
        // Feedback submission is the one call in this client that can produce an irreversible,
        // public side effect: a GitHub issue in the project's repository. RemoveAllResilienceHandlers
        // strips the standard pipeline that AddServiceDefaults installs on every named client
        // through ConfigureHttpClientDefaults, whose retry strategy re-sends on 5xx, 408, 429, and
        // HttpRequestException up to three times.
        //
        // The server's ledger makes a duplicate POST safe — a repeated preview token replays its
        // receipt rather than filing twice — so this is not the last line of defence. It is here
        // because of 429: a transport that silently re-sends a rate-limited submission ignores the
        // Retry-After the server just computed, turns one press of Submit into four requests, and
        // makes the honest wait shown to the learner a fiction.
#pragma warning disable EXTEXP0001
        services.AddHttpClient<IFeedbackApiClient, FeedbackApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        // Coach turns are bounded server-side by a 45s request timeout; allow a little headroom
        // so a slow-but-valid turn surfaces the server's typed stop reason instead of a client abort.
        services.AddHttpClient<ICoachApiClient, CoachApiClient>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(60);
        })
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()
            // Availability and every session read are keyed to the learner's local plan date.
            // Without this header the API resolves its plan-date context to UTC and reports the
            // coach unavailable after the learner's local evening.
            .AddHttpMessageHandler<PlanTimeZoneHeaderHandler>();
        services.AddSingleton<IAiGatewayClient, AiGatewayClient>();
        services.AddSingleton<ISpeechGatewayClient, SpeechGatewayClient>();
    }

    /// <summary>
    /// Registers <see cref="ApiActivityHandler"/> as transient (the required lifetime
    /// for <see cref="DelegatingHandler"/>s consumed by <c>HttpClientFactory</c>).
    /// Safe to call multiple times — uses TryAdd semantics so the several
    /// entry points (<c>AddApiClients</c>, <c>AddAuthServices</c>, <c>AddSyncServices</c>,
    /// and the <c>VersionCheckService</c> registration) don't step on each other.
    /// </summary>
    internal static IServiceCollection TryAddApiActivityHandler(this IServiceCollection services)
    {
        services.TryAddTransient<ApiActivityHandler>();
        return services;
    }

    class SyncLogger(ILogger<SyncLogger> logger) : ISyncLogger
    {
        private readonly ILogger<SyncLogger> _logger = logger;

        public void Error(string message)
        {
            _logger.LogError("Sync: {message}", message);
        }

        public void Info(string message)
        {
            _logger.LogInformation("Sync: {message}", message);
        }

        public void Trace(string message)
        {
            _logger.LogTrace("Sync: {message}", message);
        }

        public void Warning(string message)
        {
            _logger.LogWarning("Sync: {message}", message);
        }
    }

}
