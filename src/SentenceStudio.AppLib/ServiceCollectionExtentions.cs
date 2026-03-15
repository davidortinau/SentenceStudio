using CoreSync;
using CoreSync.Http.Client;
using CoreSync.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Agents;
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
                    .Table<LearningResource>("LearningResource", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<VocabularyWord>("VocabularyWord", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<ResourceVocabularyMapping>("ResourceVocabularyMapping", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<Challenge>("Challenge", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<Conversation>("Conversation", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<ConversationChunk>("ConversationChunk", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<UserProfile>("UserProfile", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SkillProfile>("SkillProfile", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<VocabularyList>("VocabularyList", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<VocabularyProgress>("VocabularyProgress", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<VocabularyLearningContext>("VocabularyLearningContext", syncDirection: SyncDirection.UploadAndDownload)
                    ;

            return new SqliteSyncProvider(configurationBuilder.Build(), ProviderMode.Local, new SyncLogger(serviceProvider.GetRequiredService<ILogger<SyncLogger>>()));
        });

        services.AddHttpClient("HttpClientToServer", httpClient =>
        {
            httpClient.BaseAddress = serverUri;
            httpClient.Timeout = TimeSpan.FromMinutes(10);
        })
        .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();

        services.AddCoreSyncHttpClient(options =>
        {
            options.HttpClientName = "HttpClientToServer";
            //options.UseBinaryFormat = true;
        });
    }

    public static IServiceCollection AddAuthServices(this IServiceCollection services, IConfiguration configuration)
    {
        var useEntraId = configuration.GetValue<bool>("Auth:UseEntraId");

        if (useEntraId)
        {
            services.AddSingleton<IAuthService, MsalAuthService>();
        }
        else
        {
            services.AddSingleton<IAuthService, IdentityAuthService>();
        }

        // Register a named HttpClient for auth endpoints (login, register, refresh).
        // Uses the same API base URL as other clients but without the auth handler
        // to avoid a circular dependency (auth client cannot require auth).
        var apiBaseUrl = configuration.GetValue<string>("ApiBaseUrl");
        if (!string.IsNullOrEmpty(apiBaseUrl))
        {
            services.AddHttpClient("AuthClient", client =>
            {
                client.BaseAddress = new Uri(apiBaseUrl);
            });
        }

        services.AddTransient<AuthenticatedHttpMessageHandler>();
        return services;
    }

    public static void AddApiClients(this IServiceCollection services, Uri baseUri)
    {
        services.AddHttpClient<IAiApiClient, AiApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient<ISpeechApiClient, SpeechApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient<IPlansApiClient, PlansApiClient>(client => client.BaseAddress = baseUri)
            .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
        services.AddSingleton<IAiGatewayClient, AiGatewayClient>();
        services.AddSingleton<ISpeechGatewayClient, SpeechGatewayClient>();
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
