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
using SentenceStudio.Services.Agents;
using SentenceStudio.Services.Observability;
using SentenceStudio.Shared.Models;


namespace SentenceStudio;

public static class ServiceCollectionExtentions
{
    /// <summary>
    /// Registers the multi-agent conversation services.
    /// </summary>
    public static IServiceCollection AddConversationAgentServices(this IServiceCollection services)
    {
        services.AddSingleton<VocabularyLookupTool>();
        services.AddScoped<IConversationAgentService, ConversationAgentService>();
        return services;
    }

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

        services.AddHttpClient<IAiApiClient, AiApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient<ISpeechApiClient, SpeechApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient<IPlansApiClient, PlansApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient<IFeedbackApiClient, FeedbackApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<ApiActivityHandler>()
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
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
