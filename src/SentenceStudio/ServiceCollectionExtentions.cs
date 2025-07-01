using CoreSync;
using CoreSync.Http.Client;
using CoreSync.Sqlite;
using Microsoft.Extensions.Logging;
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
                    .Table<SentenceStudio.Shared.Models.LearningResource>(syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.VocabularyWord>(syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.Challenge>(syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.Conversation>(syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.ConversationChunk>(syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.UserProfile>(syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.SkillProfile>(syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.VocabularyList>(syncDirection: SyncDirection.UploadAndDownload)
                    ;

            return new SqliteSyncProvider(configurationBuilder.Build(), ProviderMode.Local, new SyncLogger(serviceProvider.GetRequiredService<ILogger<SyncLogger>>()));
        });

        services.AddHttpClient("HttpClientToServer", httpClient =>
        {
            httpClient.BaseAddress = serverUri;
            httpClient.Timeout = TimeSpan.FromMinutes(10);
        });

        services.AddCoreSyncHttpClient(options =>
        {
            options.HttpClientName = "HttpClientToServer";
            //options.UseBinaryFormat = true;
        });
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
