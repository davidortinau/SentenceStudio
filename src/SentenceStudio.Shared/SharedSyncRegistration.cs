using CoreSync;
using CoreSync.Sqlite;
using SentenceStudio.Shared.Models;

namespace SentenceStudio;

/// <summary>
/// Single source of truth for the CoreSync table list shared across client and server providers.
/// </summary>
public static class SharedSyncRegistration
{
    public static SqliteSyncConfigurationBuilder ConfigureSyncTables(this SqliteSyncConfigurationBuilder builder)
    {
        return builder
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
            .Table<VocabularyLearningContext>("VocabularyLearningContext", syncDirection: SyncDirection.UploadAndDownload);
    }
}
