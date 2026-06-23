using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenAI;
using ElevenLabs;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;

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

        var settings = builder.Configuration.GetSection("Settings").Get<Settings>() ?? new Settings();

        var openAiApiKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
            ? Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey")!
            : settings.OpenAIKey ?? "not-configured";

        // Resilient HttpClient for OpenAI — MAUI doesn't call AddServiceDefaults so
        // we add explicit Polly resilience (429/5xx retry, circuit breaker, timeout).
        builder.Services.AddHttpClient("openai")
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(300);
            });

        // Default (fast) + keyed fast/reasoning chat clients, all pointed at the configured
        // (Foundry) endpoint. See AiClientRegistration / AiTier.
        builder.Services.AddTieredChatClients(builder.Configuration, openAiApiKey);

        var elevenLabsKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
            ? Environment.GetEnvironmentVariable("ElevenLabsKey")!
            : settings.ElevenLabsKey ?? "not-configured";

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

    // Gate the UnhandledException subscription so hot-reload / re-init cannot double-wire
    // the handler. Interlocked.Exchange makes this safe even if two init paths race.
    private static int _unhandledExceptionWired;

    public static MauiApp InitializeApp(MauiApp app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MauiProgram");
        logger.LogDebug("✅ MauiApp built successfully");

        // Wire unhandled-exception capture → OTel log pipeline (→ Azure Monitor in Release).
        // MauiExceptions normalizes iOS/MacCatalyst/Android/Windows/Desktop platform handlers into a single
        // event; we attach ONE subscriber here. Best-effort ForceFlush on the three OTel providers so
        // the crash record has a chance to reach the exporter before the process dies.
        if (Interlocked.Exchange(ref _unhandledExceptionWired, 1) == 0)
        {
            var crashLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SentenceStudio.UnhandledException");
            var loggerProvider = app.Services.GetService<LoggerProvider>();
            var tracerProvider = app.Services.GetService<TracerProvider>();
            var meterProvider = app.Services.GetService<MeterProvider>();
            MauiExceptions.UnhandledException += (sender, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    crashLogger.LogCritical(ex, "Unhandled exception (isTerminating={IsTerminating})", args.IsTerminating);

                    // Parallel flush bounded by a shared ~3s deadline. Serial 3s+3s+3s risked a 9s
                    // worst case that exceeds the iOS watchdog (~5-10s) on a crash path. Each
                    // provider gets 2.5s of its own (hard ceiling), then the WaitAll caps the
                    // total wall time at 3s regardless. All exceptions swallowed — exception-in-
                    // handler is worse than missed telemetry.
                    var flushTasks = new[]
                    {
                        Task.Run(() => { try { loggerProvider?.ForceFlush(2500); } catch { } }),
                        Task.Run(() => { try { tracerProvider?.ForceFlush(2500); } catch { } }),
                        Task.Run(() => { try { meterProvider?.ForceFlush(2500); } catch { } }),
                    };
                    try { Task.WaitAll(flushTasks, TimeSpan.FromMilliseconds(3000)); } catch { }
                }
                catch
                {
                    // Never throw from the last-chance handler.
                }
            };
        }

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

        // Pre-load auth token cache at startup (Fix G — stop spurious logouts)
        // This ensures IsSignedIn is correct and reduces the window where concurrent
        // refresh requests can race. Fire-and-forget — don't block startup.
        Task.Run(async () =>
        {
            try
            {
                var authService = app.Services.GetRequiredService<IAuthService>();
                logger.LogDebug("Pre-loading auth token cache at startup");
                await authService.SignInAsync(); // Silent restore
                logger.LogDebug("Auth token cache pre-load complete");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auth token cache pre-load failed — non-fatal");
            }
        });

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

                // Seed Number drill content (NumberContext, NumberSubMode, NumberCounter rows from embedded JSON).
                // Without this, the NumberDrill picker is empty on MAUI heads (only the Api seeds in Program.cs).
                try
                {
                    using var seedScope = app.Services.CreateScope();
                    var numberSeeder = seedScope.ServiceProvider.GetRequiredService<SentenceStudio.Services.Numbers.NumberContentSeeder>();
                    await numberSeeder.SeedAsync("ko");
                    logger.LogDebug("✅ Number drill content seeded");
                }
                catch (Exception numEx)
                {
                    logger.LogWarning(numEx, "Number content seeding failed — NumberDrill picker may be empty");
                }

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
        services.TryAddApiActivityHandler();
        services.AddHttpClient<VersionCheckService>(client =>
        {
            client.BaseAddress = new Uri("https+http://api");
        })
        .AddHttpMessageHandler<SentenceStudio.Services.Observability.ApiActivityHandler>();
    }
}
