using CoreSync;
using CoreSync.Sqlite;
#if !IOS && !ANDROID && !MACCATALYST && !MACOS
using CoreSync.PostgreSQL;
#endif
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio;

/// <summary>
/// Single source of truth for the CoreSync table list shared across client and server providers.
/// </summary>
public static class SharedSyncRegistration
{
    public const string NativeOwnerHeader = "X-SentenceStudio-Native-Owner";

    public static SqliteSyncConfigurationBuilder ConfigureSyncTables(
        this SqliteSyncConfigurationBuilder builder, bool downloadOnlyProfile = false)
    {
        return builder
            .Table<LearningResource>("LearningResource", syncDirection: SyncDirection.UploadAndDownload)
            .Table<VocabularyWord>("VocabularyWord", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ResourceVocabularyMapping>("ResourceVocabularyMapping", syncDirection: SyncDirection.UploadAndDownload)
            .Table<Challenge>("Challenge", syncDirection: SyncDirection.UploadAndDownload)
            .Table<Conversation>("Conversation", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ConversationChunk>("ConversationChunk", syncDirection: SyncDirection.UploadAndDownload)
            .Table<UserProfile>("UserProfile", syncDirection: downloadOnlyProfile
                ? SyncDirection.DownloadOnly : SyncDirection.UploadAndDownload)
            .Table<SkillProfile>("SkillProfile", syncDirection: SyncDirection.UploadAndDownload)
            .Table<VocabularyList>("VocabularyList", syncDirection: SyncDirection.UploadAndDownload)
            .Table<VocabularyProgress>("VocabularyProgress", syncDirection: SyncDirection.UploadAndDownload)
            .Table<VocabularyLearningContext>("VocabularyLearningContext", syncDirection: SyncDirection.UploadAndDownload)
            .Table<MonitoredChannel>("MonitoredChannel", syncDirection: SyncDirection.UploadAndDownload)
            .Table<VideoImport>("VideoImport", syncDirection: SyncDirection.UploadAndDownload)
            .Table<DailyPlan>("DailyPlan", syncDirection: SyncDirection.UploadAndDownload)
            .Table<DailyPlanCompletion>("DailyPlanCompletion", syncDirection: SyncDirection.UploadAndDownload)
            .Table<UserActivity>("UserActivity", syncDirection: SyncDirection.UploadAndDownload)
            .Table<DiaryEntry>("DiaryEntry", syncDirection: SyncDirection.UploadAndDownload)
            .Table<PhraseConstituent>("PhraseConstituent", syncDirection: SyncDirection.UploadAndDownload)
            // Phase 1 of syncing every learner table (Captain 2026-11-02,
            // docs/sync-all-tables-plan.md). Catalog tables stay local: fixed IDs.
            .Table<NumberAttempt>("NumberAttempt", syncDirection: SyncDirection.UploadAndDownload)
            .Table<NumberMasteryProgress>("NumberMasteryProgress", syncDirection: SyncDirection.UploadAndDownload)
            .Table<WordAssociationScore>("WordAssociationScore", syncDirection: SyncDirection.UploadAndDownload)
            // Phase 2a: tables converted to text IDs (migration 20261103015116_SyncTextIdsPhase2a).
            .Table<MinimalPair>("MinimalPair", syncDirection: SyncDirection.UploadAndDownload)
            .Table<MinimalPairSession>("MinimalPairSession", syncDirection: SyncDirection.UploadAndDownload)
            .Table<MinimalPairAttempt>("MinimalPairAttempt", syncDirection: SyncDirection.UploadAndDownload)
            .Table<StreamHistory>("StreamHistory", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ConversationMemoryState>("ConversationMemoryState", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ActivitySession>("ActivitySession", syncDirection: SyncDirection.UploadAndDownload)
            // Phase 3: tables that gained an owner (migration 20261103230000_SyncOwnersPhase3).
            // Built-in scenarios, device photos and rows with no owner stay where they are.
            .Table<SceneImage>("SceneImage", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ConversationScenario>("ConversationScenario", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ExampleSentence>("ExampleSentence", syncDirection: SyncDirection.UploadAndDownload);
    }

#if !IOS && !ANDROID && !MACCATALYST && !MACOS
    public static PostgreSQLSyncConfigurationBuilder ConfigureSyncTables(this PostgreSQLSyncConfigurationBuilder builder)
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
            .Table<VocabularyLearningContext>("VocabularyLearningContext", syncDirection: SyncDirection.UploadAndDownload)
            .Table<MonitoredChannel>("MonitoredChannel", syncDirection: SyncDirection.UploadAndDownload)
            .Table<VideoImport>("VideoImport", syncDirection: SyncDirection.UploadAndDownload)
            .Table<DailyPlan>("DailyPlan", syncDirection: SyncDirection.UploadAndDownload)
            .Table<DailyPlanCompletion>("DailyPlanCompletion", syncDirection: SyncDirection.UploadAndDownload)
            .Table<UserActivity>("UserActivity", syncDirection: SyncDirection.UploadAndDownload)
            .Table<DiaryEntry>("DiaryEntry", syncDirection: SyncDirection.UploadAndDownload)
            .Table<PhraseConstituent>("PhraseConstituent", syncDirection: SyncDirection.UploadAndDownload)
            // Phase 1 of syncing every learner table (Captain 2026-11-02,
            // docs/sync-all-tables-plan.md). Catalog tables stay local: fixed IDs.
            .Table<NumberAttempt>("NumberAttempt", syncDirection: SyncDirection.UploadAndDownload)
            .Table<NumberMasteryProgress>("NumberMasteryProgress", syncDirection: SyncDirection.UploadAndDownload)
            .Table<WordAssociationScore>("WordAssociationScore", syncDirection: SyncDirection.UploadAndDownload)
            // Phase 2a: tables converted to text IDs (migration 20261103015116_SyncTextIdsPhase2a).
            .Table<MinimalPair>("MinimalPair", syncDirection: SyncDirection.UploadAndDownload)
            .Table<MinimalPairSession>("MinimalPairSession", syncDirection: SyncDirection.UploadAndDownload)
            .Table<MinimalPairAttempt>("MinimalPairAttempt", syncDirection: SyncDirection.UploadAndDownload)
            .Table<StreamHistory>("StreamHistory", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ConversationMemoryState>("ConversationMemoryState", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ActivitySession>("ActivitySession", syncDirection: SyncDirection.UploadAndDownload)
            // Phase 3: tables that gained an owner (migration 20261103230000_SyncOwnersPhase3).
            // Built-in scenarios, device photos and rows with no owner stay where they are.
            .Table<SceneImage>("SceneImage", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ConversationScenario>("ConversationScenario", syncDirection: SyncDirection.UploadAndDownload)
            .Table<ExampleSentence>("ExampleSentence", syncDirection: SyncDirection.UploadAndDownload);
    }
#endif
}
