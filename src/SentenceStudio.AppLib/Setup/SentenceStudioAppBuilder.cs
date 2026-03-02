using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using ElevenLabs;
using SentenceStudio.Abstractions;
using SentenceStudio.Services.LanguageSegmentation;

namespace SentenceStudio;

public static class SentenceStudioAppBuilder
{
    public static MauiAppBuilder UseSentenceStudioApp(this MauiAppBuilder builder)
    {
        builder
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("Segoe-Ui-Bold.ttf", "SegoeBold");
                fonts.AddFont("Segoe-Ui-Regular.ttf", "SegoeRegular");
                fonts.AddFont("Segoe-Ui-Semibold.ttf", "SegoeSemibold");
                fonts.AddFont("Segoe-Ui-Semilight.ttf", "SegoeSemilight");
                fonts.AddFont("bm_yeonsung.ttf", "Yeonsung");
                fonts.AddFont("fa_solid.ttf", FontAwesome.FontFamily);
                fonts.AddFont("FluentSystemIcons-Regular.ttf", FluentUI.FontFamily);
                fonts.AddFont("Manrope-Regular.ttf", "Manrope");
                fonts.AddFont("Manrope-SemiBold.ttf", "ManropeSemibold");
                fonts.AddFont("MaterialSymbols.ttf", MaterialSymbolsFont.FontFamily);
            });

        RegisterServices(builder.Services);

        var openAiApiKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
            ? Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey")!
            : builder.Configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

        builder.Services
            .AddChatClient(new OpenAIClient(openAiApiKey).GetChatClient("gpt-4o-mini").AsIChatClient())
            .UseLogging();

        var elevenLabsKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
            ? Environment.GetEnvironmentVariable("ElevenLabsKey")!
            : builder.Configuration.GetRequiredSection("Settings").Get<Settings>().ElevenLabsKey;

        builder.Services.AddSingleton(new ElevenLabsClient(elevenLabsKey));

        // --- CoreSync setup ---
        var dbPath = Constants.DatabasePath;
        builder.Services.AddDataServices(dbPath);
        builder.Services.AddSyncServices(dbPath, new Uri($"http://{(DeviceInfo.Current.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost")}:5240"));

        var apiBaseUrl = builder.Configuration.GetValue<string>("ApiBaseUrl")
            ?? $"http://{(DeviceInfo.Current.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost")}:5001";
        builder.Services.AddApiClients(new Uri(apiBaseUrl));
        builder.Services.AddSingleton<SentenceStudio.Services.ISyncService, SentenceStudio.Services.SyncService>();

        // Register Multi-Agent Conversation Services
        builder.Services.AddConversationAgentServices();

        // Register Minimal Pair repositories
        builder.Services.AddScoped<SentenceStudio.Repositories.MinimalPairRepository>();
        builder.Services.AddScoped<SentenceStudio.Repositories.MinimalPairSessionRepository>();

        return builder;
    }

    public static MauiApp InitializeApp(MauiApp app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MauiProgram");
        logger.LogDebug("✅ MauiApp built successfully");

        // CRITICAL: Initialize database schema SYNCHRONOUSLY before app starts
        logger.LogDebug("🚀 CHECKPOINT 1: About to get ISyncService");

        SentenceStudio.Services.ISyncService syncService;
        try
        {
            syncService = app.Services.GetRequiredService<SentenceStudio.Services.ISyncService>();
            logger.LogDebug("✅ CHECKPOINT 2: Got ISyncService successfully");

            logger.LogDebug("🚀 CHECKPOINT 3: Starting InitializeDatabaseAsync with Wait()");
            Task.Run(async () => await syncService.InitializeDatabaseAsync()).Wait();
            logger.LogDebug("✅ CHECKPOINT 4: Database initialization complete");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ FATAL ERROR in database initialization");
            throw;
        }

        Task.Run(async () =>
        {
            try
            {
                logger.LogDebug("🚀 Starting async database initialization");
                var backgroundSyncService = app.Services.GetRequiredService<SentenceStudio.Services.ISyncService>();

                await backgroundSyncService.InitializeDatabaseAsync();
                logger.LogDebug("✅ Database initialization complete");

                var scenarioService = app.Services.GetRequiredService<SentenceStudio.Services.IScenarioService>();
                await scenarioService.SeedPredefinedScenariosAsync();
                logger.LogDebug("✅ Conversation scenarios seeded");

                await backgroundSyncService.TriggerSyncAsync();
                logger.LogInformation("[CoreSync] Background sync completed successfully");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CoreSync] Background sync failed");
            }
        });

        Connectivity.Current.ConnectivityChanged += (s, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var connectivitySyncService = app.Services.GetRequiredService<SentenceStudio.Services.ISyncService>();
                        await connectivitySyncService.TriggerSyncAsync();
                        logger.LogInformation("[CoreSync] Connectivity sync completed successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[CoreSync] Sync on connectivity failed");
                    }
                });
            }
        };

        return app;
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IFileSystemService, MauiFileSystemService>();
        services.AddSingleton<IPreferencesService, MauiPreferencesService>();
        services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
        services.AddSingleton<IAudioPlaybackService, MauiAudioPlaybackService>();
        services.AddSingleton<IConnectivityService, MauiConnectivityService>();

        services.AddSingleton<ThemeService>();

        services.AddSingleton<TeacherService>();
        services.AddSingleton<ConversationService>();
        services.AddSingleton<AiService>();
        services.AddSingleton<SceneImageService>();
        services.AddSingleton<ClozureService>();
        services.AddSingleton<StorytellerService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<ShadowingService>();
        services.AddSingleton<VideoWatchingService>();
        services.AddSingleton<AudioAnalyzer>();
        services.AddSingleton<YouTubeImportService>();
        services.AddSingleton<ElevenLabsSpeechService>();
        services.AddSingleton<DataExportService>();
        services.AddSingleton<NameGenerationService>();
        services.AddSingleton<VocabularyQuizPreferences>();
        services.AddSingleton<SpeechVoicePreferences>();
        services.AddSingleton<Services.Speech.IVoiceDiscoveryService, Services.Speech.VoiceDiscoveryService>();

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

        services.AddSingleton<StoryRepository>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<UserActivityRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<StreamHistoryRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<VocabularyLearningContextRepository>();
        services.AddSingleton<VocabularyProgressService>();
        services.AddSingleton<IVocabularyProgressService>(provider => provider.GetRequiredService<VocabularyProgressService>());
        services.AddSingleton<SmartResourceService>();

        services.AddSingleton<ScenarioRepository>();
        services.AddSingleton<IScenarioService, ScenarioService>();

        services.AddSingleton<EncodingStrengthCalculator>();
        services.AddSingleton<ExampleSentenceRepository>();
        services.AddSingleton<VocabularyEncodingRepository>();
        services.AddSingleton<VocabularyFilterService>();

        services.AddSingleton<ISearchQueryParser, SearchQueryParser>();

        services.AddSingleton<SentenceStudio.Services.Progress.ProgressCacheService>();
        services.AddSingleton<SentenceStudio.Services.Progress.IProgressService, SentenceStudio.Services.Progress.ProgressService>();
        services.AddSingleton<SentenceStudio.Services.Timer.IActivityTimerService, SentenceStudio.Services.Timer.ActivityTimerService>();

        services.AddSingleton<SentenceStudio.Services.PlanGeneration.DeterministicPlanBuilder>();
        services.AddSingleton<SentenceStudio.Services.PlanGeneration.ILlmPlanGenerationService, SentenceStudio.Services.Api.ApiPlanGenerationService>();
        services.AddSingleton<VocabularyExampleGenerationService>();

        services.AddSingleton<ISpeechToText>(SpeechToText.Default);
        services.AddSingleton<IFileSaver>(FileSaver.Default);

        services.AddSingleton<IAppState, AppState>();
    }
}
