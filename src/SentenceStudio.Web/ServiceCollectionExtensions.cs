using CoreSync;
using CoreSync.Http.Server;
using CoreSync.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Web
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSyncServices(this IServiceCollection services, string databasePath)
        {
            services.AddCoreSyncHttpServer();
            
            // Register server-side sync provider
            services.AddSingleton<ISyncProvider>(serviceProvider =>
            {
                var connectionString = $"Data Source={databasePath}";
                var configurationBuilder = new SqliteSyncConfigurationBuilder(connectionString);
                
                // Register all the same entities as the client - using singular table names to match EF Core
                configurationBuilder
                    .Table<SentenceStudio.Shared.Models.LearningResource>("LearningResource", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.VocabularyWord>("VocabularyWord", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.Challenge>("Challenge", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.Conversation>("Conversation", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.ConversationChunk>("ConversationChunk", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.UserProfile>("UserProfile", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.SkillProfile>("SkillProfile", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.VocabularyList>("VocabularyList", syncDirection: SyncDirection.UploadAndDownload)
                    .Table<SentenceStudio.Shared.Models.ResourceVocabularyMapping>("ResourceVocabularyMapping", syncDirection: SyncDirection.UploadAndDownload);

                return new SqliteSyncProvider(configurationBuilder.Build(), ProviderMode.Remote, new ServerSyncLogger(serviceProvider.GetRequiredService<ILogger<ServerSyncLogger>>()));
            });
            
            return services;
        }
        
        private class ServerSyncLogger(ILogger<ServerSyncLogger> logger) : ISyncLogger
        {
            private readonly ILogger<ServerSyncLogger> _logger = logger;

            public void Error(string message)
            {
                _logger.LogError("Server Sync: {message}", message);
            }

            public void Info(string message)
            {
                _logger.LogInformation("Server Sync: {message}", message);
            }

            public void Trace(string message)
            {
                _logger.LogTrace("Server Sync: {message}", message);
            }

            public void Warning(string message)
            {
                _logger.LogWarning("Server Sync: {message}", message);
            }
        }
    }
}
