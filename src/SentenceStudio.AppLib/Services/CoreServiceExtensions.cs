using SentenceStudio.Repositories;
using SentenceStudio.Services.LanguageSegmentation;
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
        services.AddSingleton<AiService>();
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

        // Repositories
        services.AddSingleton<StoryRepository>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<UserActivityRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<StreamHistoryRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<VocabularyLearningContextRepository>();
        services.AddSingleton<VocabularyProgressService>();
        services.AddSingleton<IVocabularyProgressService>(provider =>
            provider.GetRequiredService<VocabularyProgressService>());
        services.AddSingleton<SmartResourceService>();

        services.AddScoped<MinimalPairRepository>();
        services.AddScoped<MinimalPairSessionRepository>();

        services.AddSingleton<ScenarioRepository>();
        services.AddSingleton<IScenarioService, ScenarioService>();

        services.AddSingleton<EncodingStrengthCalculator>();
        services.AddScoped<ExampleSentenceRepository>();
        services.AddScoped<VocabularyEncodingRepository>();
        services.AddScoped<VocabularyFilterService>();

        services.AddSingleton<ISearchQueryParser, SearchQueryParser>();

        // Progress & timing
        services.AddSingleton<ProgressCacheService>();
        services.AddSingleton<IProgressService, ProgressService>();
        services.AddSingleton<Timer.IActivityTimerService, Timer.ActivityTimerService>();

        // Plan generation
        services.AddSingleton<DeterministicPlanBuilder>();
        services.AddSingleton<ILlmPlanGenerationService, Api.ApiPlanGenerationService>();
        services.AddSingleton<VocabularyExampleGenerationService>();

        // App state
        services.AddSingleton<IAppState, AppState>();

        return services;
    }
}
