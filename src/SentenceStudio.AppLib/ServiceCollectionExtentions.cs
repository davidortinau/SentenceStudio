using CoreSync;
using CoreSync.Http.Client;
using CoreSync.Sqlite;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Observability;
using SentenceStudio.Shared.Models;


namespace SentenceStudio;

public static class ServiceCollectionExtentions
{
    /// <summary>The named HttpClient CoreSync uses to reach the API.</summary>
    public const string SyncHttpClientName = "HttpClientToServer";

    public static void AddSyncServices(
        this IServiceCollection services, string databasePath, Uri serverUri,
        bool downloadOnlyProfile = false)
    {
        services.AddSingleton<ISyncProvider>(serviceProvider =>
        {
            var connectionString = $"Data Source={databasePath}";
            var configurationBuilder =
                new SqliteSyncConfigurationBuilder(connectionString)
                    .ConfigureSyncTables(downloadOnlyProfile);

            return new SqliteSyncProvider(configurationBuilder.Build(), ProviderMode.Local, new SyncLogger(serviceProvider.GetRequiredService<ILogger<SyncLogger>>()));
        });

        services.TryAddApiActivityHandler();

        services.AddHttpClient(SyncHttpClientName, httpClient =>
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
            options.HttpClientName = SyncHttpClientName;
            //options.UseBinaryFormat = true;
        });
    }

    /// <summary>
    /// Owner-checked sync for a head with one database for every account (the MAUI heads,
    /// Captain 2026-11-04, decision 1). Call after <see cref="AddSyncServices"/>. Every sync
    /// request carries the signed-in learner's verified owner header (none is sent without
    /// one); uploads keep device-only rows and other learners' rows on the device
    /// (<see cref="OwnerCheckedSyncProvider"/>); newly synced tables queue and re-request the
    /// learner's rows. The server binds this install's store to the first learner that syncs;
    /// a second account on the same install is refused and its work stays on the device.
    /// Native heads use <c>AddOwnerBoundNativeSyncReadiness</c> instead.
    /// </summary>
    public static IServiceCollection AddOwnerCheckedSync(this IServiceCollection services)
    {
        var localRegistration = services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(ISyncProvider))
            ?? throw new InvalidOperationException("Call AddSyncServices before AddOwnerCheckedSync.");
        var createLocal = localRegistration.ImplementationFactory
            ?? throw new InvalidOperationException("The local sync provider must be factory-registered.");
        services[services.IndexOf(localRegistration)] = ServiceDescriptor.Singleton<ISyncProvider>(provider =>
        {
            var local = (ISyncProvider)createLocal(provider);
            var connectionString = (local as SqliteSyncProvider)?.Configuration.ConnectionString
                ?? throw new InvalidOperationException("Owner-checked sync needs the local SQLite store.");
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString).Options;
            return new OwnerCheckedSyncProvider(
                local, () => new ApplicationDbContext(options),
                // Resolved when sync runs, not here. The owner source needs the sign-in service,
                // which takes ISyncService, which takes this provider: resolving it during
                // construction is a cycle that stops the MAUI app at launch.
                new DeferredSyncOwnerSource(provider),
                provider.GetRequiredService<ILogger<OwnerCheckedSyncProvider>>());
        });

        services.TryAddSingleton<ISyncOwnerSource, SignedInSyncOwnerSource>();
        services.TryAddTransient<SyncOwnerHeaderHandler>();
        // Innermost, so it sets the bearer and the owner header from the same verified token.
        services.AddHttpClient(SyncHttpClientName).AddHttpMessageHandler<SyncOwnerHeaderHandler>();
        services.Replace(ServiceDescriptor.Singleton<ISyncResendRequester, OwnerCheckedSyncResendRequester>());
        return services;
    }

    /// <summary>The registered <see cref="ISyncOwnerSource"/>, resolved on each use rather than at construction.</summary>
    private sealed class DeferredSyncOwnerSource(IServiceProvider services) : ISyncOwnerSource
    {
        public Task<SyncOwnerCredentials?> GetAsync(CancellationToken cancellationToken) =>
            services.GetRequiredService<ISyncOwnerSource>().GetAsync(cancellationToken);
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
        // Speech synthesis is a paid ElevenLabs generation on every attempt the API does not serve
        // from its cache. The standard pipeline (ServiceDefaults) retries 5xx and 408, which would pay
        // for one clip several times, so neither synthesis client has resilience handlers. Each
        // request is one attempt; the page reports a failure and the learner can play again.
        // A word or sentence gets two minutes (the per-attempt bound MAUI had before), so a stalled
        // call frees the play button well before the five minutes Reading's long transcripts need.
#pragma warning disable EXTEXP0001
        services.AddHttpClient<ISpeechApiClient, SpeechApiClient>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromMinutes(2);
        })
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()
            .RemoveAllResilienceHandlers();
        // The voice list is an idempotent GET: it keeps the standard resilience pipeline.
        services.AddHttpClient<ISpeechVoicesApiClient, SpeechVoicesApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        // Reading's timestamped audio: a long transcript can take minutes on the server.
        services.AddHttpClient<ITimestampedSpeechApiClient, TimestampedSpeechApiClient>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromMinutes(5);
        })
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()
            .RemoveAllResilienceHandlers();
        // Mints a short-lived realtime client secret for device-side voice sessions. Not retried:
        // each secret is single-purpose, so a retry only burns the per-learner issuance budget.
        services.AddHttpClient<IRealtimeApiClient, RealtimeApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>()
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
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
