using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using ElevenLabs;
using SentenceStudio.Abstractions;

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

        // Use Aspire service discovery: "https+http://servicename" is resolved by
        // MauiServiceDefaults → AddServiceDiscovery(). When launched from Aspire,
        // env vars (services__api__https__0 etc.) override the config. When launched
        // manually, the Services section in appsettings.json provides fallback URLs.
        // CoreSync server is hosted on the API (not the separate 'web' service) so
        // mobile clients can reach it through the existing dev tunnel / service discovery.
        var syncServerUri = new Uri("https+http://api");
        builder.Services.AddSyncServices(dbPath, syncServerUri);

        var apiBaseUri = new Uri("https+http://api");

        // Auth services — pass resolved API URI so AuthClient always has a BaseAddress
        builder.Services.AddAuthServices(builder.Configuration, apiBaseUri);

        builder.Services.AddApiClients(apiBaseUri);
        builder.Services.AddSingleton<SentenceStudio.Services.ISyncService, SentenceStudio.Services.SyncService>();

        // Register Multi-Agent Conversation Services
        builder.Services.AddConversationAgentServices();

        // Register Minimal Pair repositories
        builder.Services.AddScoped<SentenceStudio.Repositories.MinimalPairRepository>();
        builder.Services.AddScoped<SentenceStudio.Repositories.MinimalPairSessionRepository>();

        // Apply saved DisplayLanguage from UserProfile at MAUI launch (client only — single-user process).
        builder.Services.AddSingleton<IMauiInitializeService, LocalizationInitializer>();

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
        // Platform-specific abstractions (MAUI implementations)
        services.AddSingleton<IFileSystemService, MauiFileSystemService>();
        services.AddSingleton<IPreferencesService, MauiPreferencesService>();
        services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
        services.AddSingleton<IAudioPlaybackService, MauiAudioPlaybackService>();
        services.AddSingleton<IConnectivityService, MauiConnectivityService>();

        // Shared core services
        services.AddSentenceStudioCoreServices();

        // MAUI-only services
        services.AddSingleton<ISpeechToText>(SpeechToText.Default);
        services.AddSingleton<IFileSaver>(FileSaver.Default);
        
        // Release notes service (reads from embedded resources in Shared assembly)
        services.AddSingleton<ReleaseNotesService>();

        // Version check service — calls API to detect available updates (mobile only)
        services.AddHttpClient<VersionCheckService>(client =>
        {
            client.BaseAddress = new Uri("https+http://api");
        });
    }
}
