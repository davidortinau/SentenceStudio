using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models.DailyPlanGeneration;
using AndroidContext = global::Android.Content.Context;

namespace SentenceStudio.Native.Android;

internal sealed class AndroidServiceHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _databasePath;
    private readonly bool _preview;

    private AndroidServiceHost(ServiceProvider provider, string databasePath, bool preview)
    {
        _provider = provider;
        _databasePath = databasePath;
        _preview = preview;
    }

    public IServiceScope CreateScope() => _provider.CreateScope();

    public static AndroidServiceHost Create(AndroidContext context) =>
        CreateCore(context, preview: false);

#if DEBUG
    public static AndroidServiceHost CreatePreview(AndroidContext context)
    {
        if (context.PackageName != "com.simplyprofound.sentencestudio.compose.debug")
            throw new InvalidOperationException("Preview is limited to the isolated Debug package.");
        return CreateCore(context, preview: true);
    }
#endif

    private static AndroidServiceHost CreateCore(AndroidContext context, bool preview)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not MainActivity activity)
            throw new ArgumentException("The Android service host requires MainActivity.",
                nameof(context));
        var apiUri = GetApiUri();
        var dataRoot = context.FilesDir?.AbsolutePath
            ?? throw new DirectoryNotFoundException("Android app-private storage is unavailable.");
#if !DEBUG
        AndroidReplacementGuard.RefuseLegacyDatabase(dataRoot);
#endif
        var databasePath = Path.Combine(dataRoot, preview
            ? "sentence-studio-debug-preview-word-owner.sqlite"
            : "sentence-studio-native.sqlite");

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new AndroidLogProvider()));
        services.AddSingleton<IPreferencesService>(new AndroidPreferences(context, preview));
        if (!preview)
            services.AddSingleton<ISecureStorageService>(new AndroidSecureStorage(context));
        services.AddSingleton<IConnectivityService>(new AndroidConnectivity(context));
        services.AddSingleton<IFileSystemService>(new AndroidFiles(context));
        services.AddSingleton<DeviceUserScopeProvider>();
        services.AddSingleton<IUserScopeProvider>(sp =>
            sp.GetRequiredService<DeviceUserScopeProvider>());

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddHttpClient("AuthClient", client =>
        {
            client.BaseAddress = apiUri;
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        if (!preview)
        {
            services.AddSyncServices(databasePath, apiUri, downloadOnlyProfile: true);
            services.AddSingleton<ISyncService, SyncService>();
            services.AddOwnerBoundNativeAiGateway(apiUri);
        }

        services.AddTransient<IPlanDateContext>(_ =>
            new PlanDateContext(TimeZoneInfo.Local));
#if DEBUG
        if (preview)
        {
            services.AddSingleton<DebugPreviewPlanGenerator>();
            services.AddSingleton<IDeterministicPlanGenerator>(sp =>
                sp.GetRequiredService<DebugPreviewPlanGenerator>());
        }
        else
#endif
        {
            AndroidPlanServiceRegistrations.AddProduction(services);
        }
        AndroidPlanServiceRegistrations.AddCommon(services);
        services.AddScoped<UserActivityRepository>();
        services.AddScoped<VocabularyLearningContextRepository>();
        services.AddScoped<VocabularyProgressService>();
        services.AddScoped<IVocabularyProgressService>(sp =>
            sp.GetRequiredService<VocabularyProgressService>());
        services.AddSingleton<ProgressCacheService>();
        services.AddScoped<ILlmPlanGenerationService, UnavailableNativeLlmPlanGeneration>();
        services.AddScoped<IProgressService, ProgressService>();
        services.AddScoped<VocabQuizLaunchValidator>();
        services.AddScoped<ActivitySessionService>();
        services.AddScoped<IActivitySessionService>(sp =>
            sp.GetRequiredService<ActivitySessionService>());
        services.AddSingleton<SpeechVoicePreferences>();
        services.AddSingleton<VocabularyQuizPreferences>();
        services.AddScoped<IPostLoginRouter, PostLoginRouter>();
        services.AddSingleton<INativePlanCreation, NativePlanCreationAdapter>();
        services.AddSingleton<INativeQuizAttemptCommitter, NativeQuizAttemptCommitterAdapter>();
        services.AddScoped<INativeQuizChoices, NativeQuizChoicesAdapter>();
        services.AddScoped<INativeQuizEligibility, NativeQuizEligibilityAdapter>();
        services.AddSingleton<INativeQuizGrader, NativeQuizGraderAdapter>();
        services.AddSentenceStudioNativeSlice();
        services.AddSentenceStudioNativeIdentityAuth();
        services.AddSingleton(sp => new AndroidQuizMedia(
            activity, apiUri,
            sp.GetRequiredService<DeviceUserScopeProvider>(),
            sp.GetRequiredService<IPreferencesService>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<ILogger<AndroidQuizMedia>>()));
        services.Replace(ServiceDescriptor.Singleton<INativeQuizMedia>(sp =>
            sp.GetRequiredService<AndroidQuizMedia>()));
        if (!preview)
        {
            services.AddSentenceStudioNativeProductionAdapters();
            services.AddOwnerBoundNativeSyncReadiness();
        }

        return new AndroidServiceHost(services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }), databasePath, preview);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var existingDatabase = File.Exists(_databasePath);
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
#if DEBUG
        if (_preview)
        {
            if (!existingDatabase)
                await db.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }
#endif
        var migrations = db.Database.GetMigrations();
        if (!migrations.Any())
            throw new InvalidOperationException(
                "No Android SQLite migrations were discovered; native startup refused.");

        // An existing database is copied and verified before pending migrations apply;
        // if the copy fails, nothing is migrated and startup fails closed.
        // Copies go to the no-backup folder so they don't count toward Auto Backup's 25 MB cap.
        await SqliteMigrationBackup.MigrateWithBackupAsync(db, _databasePath,
            scope.ServiceProvider.GetRequiredService<ILogger<AndroidServiceHost>>(), cancellationToken,
            global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath);
    }

    internal sealed class UnavailableNativeLlmPlanGeneration : ILlmPlanGenerationService
    {
        public Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(
                "Native AI plan generation requires a configured API-backed shared client.");
    }

    public void Dispose() => _provider.Dispose();

    private static Uri GetApiUri()
    {
        var value = typeof(AndroidServiceHost).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "SentenceStudioNativeApiUrl")?.Value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/"
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
            throw new InvalidOperationException(
                "Native API URL must be an absolute root URL without credentials or query.");
#if DEBUG
        if (uri.Scheme is not ("http" or "https")
            || uri.Scheme == "http" && uri.Host != "10.0.2.2")
            throw new InvalidOperationException(
                "Debug HTTP must target the Android emulator's isolated host alias.");
#else
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Native Release requires an HTTPS API URL.");
#endif
        return uri;
    }
}

internal sealed class AndroidLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AndroidLogger(categoryName);
    public void Dispose() { }

    private sealed class AndroidLogger(string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information
            && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = $"{category}: {formatter(state, exception)}";
            if (exception is not null)
                message = $"{message} ({exception.GetType().Name}: {exception.Message})";
            if (logLevel >= LogLevel.Error)
                global::Android.Util.Log.Error("SentenceStudio.Native", message);
            else if (logLevel == LogLevel.Warning)
                global::Android.Util.Log.Warn("SentenceStudio.Native", message);
            else
                global::Android.Util.Log.Info("SentenceStudio.Native", message);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}
