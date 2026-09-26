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
using SentenceStudio.Services.Theme;
#if DEBUG
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
#endif

namespace SentenceStudio;

public static class SentenceStudioAppBuilder
{
    public static MauiAppBuilder UseSentenceStudioApp(this MauiAppBuilder builder)
    {
#if DEBUG
        return UseSentenceStudioAppCore(builder, Constants.DatabasePath, validationConnection: null);
#else
        return UseSentenceStudioAppCore(builder, Constants.DatabasePath);
#endif
    }

#if DEBUG
    public static MauiAppBuilder UseSentenceStudioApp(
        this MauiAppBuilder builder,
        SqliteConnection validationConnection)
    {
        ArgumentNullException.ThrowIfNull(validationConnection);
        if (validationConnection.State != ConnectionState.Open
            || string.IsNullOrWhiteSpace(validationConnection.DataSource)
            || !Path.IsPathFullyQualified(validationConnection.DataSource))
        {
            throw new ArgumentException(
                "Migration validation requires an already-open connection to an absolute database path.",
                nameof(validationConnection));
        }

        return UseSentenceStudioAppCore(
            builder,
            Path.GetFullPath(validationConnection.DataSource),
            validationConnection);
    }
#endif

    private static MauiAppBuilder UseSentenceStudioAppCore(
        MauiAppBuilder builder,
        string databasePath
#if DEBUG
        , SqliteConnection validationConnection
#endif
        )
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
        var dbPath = Path.GetFullPath(databasePath);
#if DEBUG
        if (validationConnection is null)
        {
            builder.Services.AddDataServices(dbPath);
        }
        else
        {
            AddMigrationValidationDataServices(builder.Services, validationConnection);
        }
#else
        builder.Services.AddDataServices(dbPath);
#endif

        // Use Aspire service discovery: "https+http://servicename" is resolved by
        // MauiServiceDefaults → AddServiceDiscovery(). When launched from Aspire,
        // env vars (services__api__https__0 etc.) override the config. When launched
        // manually, the Services section in appsettings.json provides fallback URLs.
        // CoreSync server is hosted on the API (not the separate 'web' service) so
        // mobile clients can reach it through the existing dev tunnel / service discovery.
        var syncServerUri = new Uri("https+http://api");
#if DEBUG
        if (validationConnection is null)
        {
            builder.Services.AddSyncServices(dbPath, syncServerUri);
        }
#else
        builder.Services.AddSyncServices(dbPath, syncServerUri);
#endif

        var apiBaseUri = new Uri("https+http://api");

        // Auth services — pass resolved API URI so AuthClient always has a BaseAddress
        builder.Services.AddAuthServices(builder.Configuration, apiBaseUri);

        builder.Services.AddApiClients(apiBaseUri);
#if DEBUG
        if (validationConnection is null)
        {
            builder.Services.AddSingleton<SentenceStudio.Services.ISyncService, SentenceStudio.Services.SyncService>();
        }
        else
        {
            builder.Services.AddSingleton<SentenceStudio.Services.ISyncService>(serviceProvider =>
                new MigrationValidationSyncService(
                    serviceProvider,
                    validationConnection,
                    serviceProvider.GetRequiredService<ILogger<MigrationValidationSyncService>>()));
        }
#else
        builder.Services.AddSingleton<SentenceStudio.Services.ISyncService, SentenceStudio.Services.SyncService>();
#endif

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
#if DEBUG
        return InitializeAppCore(app, suppressAutomaticStartup: false);
#else
        return InitializeAppCore(app);
#endif
    }

#if DEBUG
    public static MauiApp InitializeApp(
        MauiApp app,
        bool suppressAutomaticStartup)
    {
        return InitializeAppCore(app, suppressAutomaticStartup);
    }
#endif

#if DEBUG
    private static MauiApp InitializeAppCore(
        MauiApp app,
        bool suppressAutomaticStartup)
#else
    private static MauiApp InitializeAppCore(MauiApp app)
#endif
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

#if DEBUG
        if (suppressAutomaticStartup)
        {
            logger.LogInformation(
                "Migration validation automatic startup suppressed after database initialization.");
            return app;
        }
#endif

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

#if DEBUG
    private static void AddMigrationValidationDataServices(
        IServiceCollection services,
        SqliteConnection validationConnection)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            // The validation session owns this already-open connection. EF must use this exact
            // handle without opening, closing, pooling, or disposing a path-based replacement.
            options.UseSqlite(validationConnection, contextOwnsConnection: false);
            options.ConfigureWarnings(warnings =>
                warnings.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            options.LogTo(
                message => System.Diagnostics.Debug.WriteLine(message),
                LogLevel.Warning);
        });
    }

    private sealed class MigrationValidationSyncService : SentenceStudio.Services.ISyncService
    {
        private static readonly (string Table, string Column)[] RequiredColumns =
        [
            ("VocabularyWord", "LexicalUnitType"),
            ("VocabularyWord", "Language"),
            ("VocabularyWord", "Lemma"),
            ("VocabularyWord", "Tags"),
            ("VocabularyWord", "MnemonicText"),
            ("VocabularyWord", "AudioPronunciationUri"),
            ("VocabularyProgress", "ExposureCount"),
            ("VocabularyProgress", "LastExposedAt"),
            ("VocabularyProgress", "CurrentStreak"),
            ("DailyPlanCompletion", "NarrativeJson"),
            ("DailyPlan", "FocusVocabularyFacts"),
            ("DailyPlan", "NarrativeFacts"),
            ("DailyPlan", "RationaleFacts"),
        ];

        private static readonly string[] RequiredTables =
        [
            "VocabularyWord",
            "VocabularyProgress",
            "PhraseConstituent",
            "DailyPlan",
            "DailyPlanCompletion",
            "UserProfile",
            "SkillProfile",
            "LearningResource",
        ];

        private static readonly (string Table, string Column, string AlterSql)[]
            ExistingMobileCompatibilityColumns =
        [
            (
                "VocabularyProgress",
                "ExposureCount",
                """
                ALTER TABLE "VocabularyProgress"
                ADD COLUMN "ExposureCount" INTEGER NOT NULL DEFAULT 0;
                """),
            (
                "VocabularyProgress",
                "LastExposedAt",
                """
                ALTER TABLE "VocabularyProgress"
                ADD COLUMN "LastExposedAt" TEXT;
                """),
            (
                "DailyPlanCompletion",
                "NarrativeJson",
                """
                ALTER TABLE "DailyPlanCompletion"
                ADD COLUMN "NarrativeJson" TEXT;
                """),
        ];

        private readonly IServiceProvider _serviceProvider;
        private readonly SqliteConnection _validationConnection;
        private readonly ILogger<MigrationValidationSyncService> _logger;
        private bool _initialized;

        internal MigrationValidationSyncService(
            IServiceProvider serviceProvider,
            SqliteConnection validationConnection,
            ILogger<MigrationValidationSyncService> logger)
        {
            _serviceProvider = serviceProvider;
            _validationConnection = validationConnection;
            _logger = logger;
        }

        public bool IsInitialSyncInProgress => false;

        public event Action InitialSyncCompleted
        {
            add { }
            remove { }
        }

        public void BeginInitialSync()
        {
        }

        public async Task InitializeDatabaseAsync()
        {
            if (_initialized)
            {
                return;
            }

            if (_validationConnection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException(
                    "Migration validation connection closed before EF migration.");
            }

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!ReferenceEquals(
                    dbContext.Database.GetDbConnection(),
                    _validationConnection))
            {
                throw new InvalidOperationException(
                    "ApplicationDbContext is not bound to the opened migration validation connection.");
            }

            _logger.LogInformation(
                "Running EF Core migrations through the opened validation database connection.");
            await dbContext.Database.MigrateAsync().ConfigureAwait(false);
            await ApplyExistingMobileCompatibilityPatchesAsync(
                _validationConnection).ConfigureAwait(false);

            if (_validationConnection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException(
                    "EF closed the caller-owned migration validation connection.");
            }

            await ValidateSchemaAsync(_validationConnection).ConfigureAwait(false);
            _initialized = true;
        }

        public Task TriggerSyncAsync() => Task.CompletedTask;

        private async Task ApplyExistingMobileCompatibilityPatchesAsync(
            SqliteConnection connection)
        {
            foreach (var (table, column, alterSql) in ExistingMobileCompatibilityColumns)
            {
                using var existsCommand = connection.CreateCommand();
                existsCommand.CommandText =
                    "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
                existsCommand.Parameters.AddWithValue("$table", table);
                existsCommand.Parameters.AddWithValue("$column", column);
                if (Convert.ToInt64(
                        await existsCommand.ExecuteScalarAsync().ConfigureAwait(false)) != 0)
                {
                    continue;
                }

                _logger.LogInformation(
                    "Applying existing mobile compatibility patch to validation database: {Table}.{Column}",
                    table,
                    column);
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = alterSql;
                await alterCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private async Task ValidateSchemaAsync(SqliteConnection connection)
        {
            var missingItems = new List<string>();

            foreach (var table in RequiredTables)
            {
                if (!await SchemaObjectExistsAsync(
                        connection,
                        "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;",
                        table).ConfigureAwait(false))
                {
                    missingItems.Add($"Table: {table}");
                }
            }

            foreach (var (table, column) in RequiredColumns)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
                command.Parameters.AddWithValue("$table", table);
                command.Parameters.AddWithValue("$column", column);
                if (Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false)) == 0)
                {
                    missingItems.Add($"{table}.{column}");
                }
            }

            if (missingItems.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Mobile schema sanity check FAILED — {missingItems.Count} missing items after migration: "
                    + string.Join(", ", missingItems));
            }

            _logger.LogInformation(
                "Mobile schema sanity check PASSED — {TableCount} tables, {ColumnCount} columns verified",
                RequiredTables.Length,
                RequiredColumns.Length);
        }

        private static async Task<bool> SchemaObjectExistsAsync(
            SqliteConnection connection,
            string sql,
            string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(
                await command.ExecuteScalarAsync().ConfigureAwait(false)) == 1;
        }
    }
#endif

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

        // Appearance state, device-scoped: one theme/mode/text-size tuple per installation, kept in
        // platform preferences. Singleton is correct on MAUI because the process is the device.
        services.AddDeviceThemePresentation();

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
