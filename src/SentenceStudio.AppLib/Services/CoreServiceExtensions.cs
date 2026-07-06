using SentenceStudio.Repositories;
using SentenceStudio.Services.LanguageSegmentation;
using SentenceStudio.Services.Numbers;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Services;

/// <summary>
/// Registers the core SentenceStudio application services shared by all hosts
/// (MAUI Hybrid and server-side Blazor). Platform-specific abstractions
/// (IFileSystemService, IPreferencesService, etc.) must be registered by
/// each host before calling this method.
/// </summary>
public static class CoreServiceExtensions
{
    public static IServiceCollection AddSentenceStudioCoreServices(this IServiceCollection services)
    {
        // Theme
        services.AddSingleton<ThemeService>();

        // Activity services
        services.AddSingleton<TeacherService>();
        services.AddSingleton<ConversationService>();
        services.AddSingleton<DiaryService>();
        services.AddSingleton<AiService>();
        services.AddSingleton<IAiService>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<SceneImageService>();
        services.AddSingleton<ClozureService>();
        services.AddSingleton<WordAssociationService>();
        services.AddSingleton<StorytellerService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<ShadowingService>();
        services.AddSingleton<VideoWatchingService>();
        services.AddSingleton<AudioAnalyzer>();
        services.AddSingleton<YouTubeImportService>();
        services.AddSingleton<ElevenLabsSpeechService>();
        services.AddSingleton<DataExportService>();
        
        // YouTube channel monitoring services
        services.AddSingleton<ChannelMonitorService>();
        services.AddSingleton<VideoImportPipelineService>();
        services.AddSingleton<IVideoImportPipeline>(sp => sp.GetRequiredService<VideoImportPipelineService>());
        services.AddSingleton<NameGenerationService>();
        services.AddSingleton<VocabularyQuizPreferences>();
        services.AddSingleton<SpeechVoicePreferences>();
        services.AddSingleton<IVoiceDiscoveryService, VoiceDiscoveryService>();

        // Language segmenters
        services.AddSingleton<KoreanLanguageSegmenter>();
        services.AddSingleton<GenericLatinSegmenter>();
        services.AddSingleton<FrenchLanguageSegmenter>();
        services.AddSingleton<GermanLanguageSegmenter>();
        services.AddSingleton<SpanishLanguageSegmenter>();
        services.AddSingleton<IEnumerable<ILanguageSegmenter>>(provider =>
            new List<ILanguageSegmenter>
            {
                provider.GetRequiredService<KoreanLanguageSegmenter>(),
                provider.GetRequiredService<GenericLatinSegmenter>(),
                provider.GetRequiredService<FrenchLanguageSegmenter>(),
                provider.GetRequiredService<GermanLanguageSegmenter>(),
                provider.GetRequiredService<SpanishLanguageSegmenter>()
            });
        services.AddSingleton<LanguageSegmenterFactory>();
        services.AddSingleton<TranscriptFormattingService>();
        services.AddSingleton<TranscriptSentenceExtractor>();

        // Data recovery (orphan re-tagging after server wipe)
        services.AddSingleton<DataRecoveryService>();

        // Repositories
        services.AddSingleton<StoryRepository>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<UserActivityRepository>();
        services.AddSingleton<DiaryEntryRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<StreamHistoryRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<VocabularyLearningContextRepository>();
        services.AddSingleton<VocabularyProgressService>();
        services.AddSingleton<IVocabularyProgressService>(provider =>
            provider.GetRequiredService<VocabularyProgressService>());
        services.AddSingleton<SmartResourceService>();

        // Post-login routing decision (issue #187) — keep the rule out of LoginPage
        // and MainLayout so it's testable and consistent.
        services.AddSingleton<IPostLoginRouter, PostLoginRouter>();

        services.AddScoped<MinimalPairRepository>();
        services.AddScoped<MinimalPairSessionRepository>();
        services.AddSingleton<ActivitySessionService>();
        services.AddSingleton<IActivitySessionService>(provider =>
            provider.GetRequiredService<ActivitySessionService>());

        services.AddSingleton<ScenarioRepository>();
        services.AddSingleton<IScenarioService, ScenarioService>();

        services.AddSingleton<EncodingStrengthCalculator>();
        services.AddScoped<ExampleSentenceRepository>();
        services.AddScoped<VocabularyEncodingRepository>();
        services.AddScoped<VocabularyFilterService>();

        services.AddSingleton<ISearchQueryParser, SearchQueryParser>();

        // Content import
        services.AddScoped<IContentImportService, ContentImportService>();
        services.AddScoped<ITranscriptSentenceHarvestService, TranscriptSentenceHarvestService>();

        // Shared ingest supporting services (cross-platform; queue is iOS-only, registered per head)
        services.AddSingleton<SharedIngestNotifier>();
        services.AddSingleton<SharedIngestDrainGate>();
        services.AddHttpClient<IWebArticleFetcher, WebArticleFetcher>();
        services.AddScoped<ISharedInboxResourceFinder, LearningResourceSharedInboxFinder>();
        services.AddScoped<IActiveUserProfileProvider, UserProfileActiveProvider>();

        // Progress & timing
        services.AddSingleton<ProgressCacheService>();
        services.AddSingleton<IProgressService, ProgressService>();

        // Plan generation — use local DeterministicPlanBuilder for rich narratives
        services.AddSingleton<DeterministicPlanBuilder>();
        services.AddSingleton<GeneratedPlanValidator>();
        services.AddSingleton<ILlmPlanGenerationService, LlmPlanGenerationService>();
        services.AddSingleton<VocabularyExampleGenerationService>();

        // New split: IDeterministicPlanGenerator is ALWAYS registered.
        // ILlmPlanGenerator is registered conditionally below, only if an
        // IChatClient is available. Callers (IPlanService, future thin
        // clients) resolve ILlmPlanGenerator as optional and fall back to
        // the deterministic generator when absent. v1 has no real LLM call;
        // see LlmPlanGenerator XML doc.
        services.AddSingleton<SentenceStudio.Services.Plans.IDeterministicPlanGenerator,
                              SentenceStudio.Services.Plans.DeterministicPlanGenerator>();
        if (services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.AI.IChatClient)))
        {
            services.AddSingleton<SentenceStudio.Services.Plans.ILlmPlanGenerator,
                                  SentenceStudio.Services.Plans.LlmPlanGenerator>();
        }

        // Plan scope + date context (device side).
        //   - DeviceUserScopeProvider wraps the single active user profile id;
        //     auth/sign-in flow calls SetActiveUser(...) after login.
        //   - IPlanDateContext is resolved on-demand from TimeZoneInfo.Local so
        //     DST shifts and travel-induced timezone changes apply immediately
        //     without re-creating the singleton.
        services.AddSingleton<SentenceStudio.Services.DeviceUserScopeProvider>();
        services.AddSingleton<SentenceStudio.Services.Plans.IUserScopeProvider>(sp =>
            sp.GetRequiredService<SentenceStudio.Services.DeviceUserScopeProvider>());
        services.AddSingleton<SentenceStudio.Services.DevicePlanDateContextProvider>();
        services.AddTransient<SentenceStudio.Services.Plans.IPlanDateContext>(sp =>
            sp.GetRequiredService<SentenceStudio.Services.DevicePlanDateContextProvider>().Current());

        // App state
        services.AddSingleton<IAppState, AppState>();

        // Backfill services
        services.AddSingleton<VocabularyClassificationBackfillService>();
        
        // Mobile schema sanity check service (DEBUG only on mobile)
        services.AddSingleton<MigrationSanityCheckService>();

        // Number activity services (Phase 1: Korean)
        services.AddSingleton<INumberItemGenerator, KoreanNumberItemGenerator>();
        services.AddSingleton<INumberAnswerGrader, KoreanNumberAnswerGrader>();
        services.AddScoped<NumberContentSeeder>();
        services.AddScoped<NumberSessionService>();
        services.AddSingleton<INumberTtsService>(sp =>
        {
            var elevenLabsService = sp.GetRequiredService<ElevenLabsSpeechService>();
            return new ElevenLabsNumberTtsAdapter(elevenLabsService);
        });
        services.AddSingleton<INumberAudioCache, NumberAudioCache>();

        return services;
    }
}
