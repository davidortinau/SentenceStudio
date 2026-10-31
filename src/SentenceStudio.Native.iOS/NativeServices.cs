using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models.DailyPlanGeneration;
using SentenceStudio;

namespace SentenceStudio.Native.iOS;

public sealed class NativeServices : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _database;

    private NativeServices(ServiceProvider provider, string database)
    {
        _provider = provider;
        _database = database;
    }

    public IServiceProvider Provider => _provider;

    public static NativeServices Create()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SentenceStudio.Native.iOS");
        Directory.CreateDirectory(folder);
        // EnsureCreated previews are not migratable; a new file keeps the old sandbox intact.
        var database = Path.Combine(folder,
            PreviewSchemaEnabled()
                ? (PreviewSurface() is "mc" or "mc-feedback" or "mc-back"
                    or "mc-resume" or "mc-details"
                    or "mc-style-feedback-correct" or "mc-style-feedback-wrong")
                    ? "sentencestudio.preview.mc.v3.sqlite"
                    : "sentencestudio.preview.v3.sqlite"
                : "sentencestudio.sqlite");
        var endpoint = NativeApiEndpoint.FromAssembly();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Information));
        // One database (and CoreSync store) per signed-in owner; the existing
        // file stays in place for the owner its rows prove.
        var preview = PreviewSchemaEnabled();
        services.AddNativeOwnerSqlite(sp => preview
            ? NativeOwnerStores.Fixed(database,
                sp.GetRequiredService<DeviceUserScopeProvider>(),
                sp.GetRequiredService<ILogger<NativeOwnerStores>>())
            : new NativeOwnerStores(folder, Path.GetFileName(database),
                sp.GetRequiredService<DeviceUserScopeProvider>(),
                sp.GetRequiredService<ILogger<NativeOwnerStores>>(),
                afterMigrate: context => context.EnableWalMode()));
        services.AddSingleton<IPreferencesService, NativePreferences>();
        services.AddSingleton<ISecureStorageService, NativeKeychain>();
        services.AddSingleton<IConnectivityService, NativeConnectivity>();
        services.AddSingleton<IFileSystemService, NativeFileSystem>();
        if (endpoint is null)
            services.AddSingleton<ISyncService, NoOpSyncService>();
        services.AddSingleton<DeviceUserScopeProvider>();
        services.AddSingleton<IUserScopeProvider>(sp =>
            sp.GetRequiredService<DeviceUserScopeProvider>());
        services.AddTransient<IPlanDateContext>(_ => new PlanDateContext(TimeZoneInfo.Local));
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<UserActivityRepository>();
        services.AddSingleton<StreamHistoryRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<VocabularyLearningContextRepository>();
        services.AddSingleton<VocabularyProgressService>();
        services.AddSingleton<IVocabularyProgressService>(sp =>
            sp.GetRequiredService<VocabularyProgressService>());
        services.AddSingleton<ProgressCacheService>();
        services.AddSingleton<IProgressService, ProgressService>();
        services.AddSingleton<DeterministicPlanBuilder>();
        services.AddSingleton<IDeterministicPlanGenerator, DeterministicPlanGenerator>();
        services.AddSingleton<ILlmPlanGenerationService, UnavailableLegacyLlmPlan>();
        services.AddSingleton<IPostLoginRouter, PostLoginRouter>();
        services.AddSingleton<VocabQuizLaunchValidator>();
        services.AddSingleton<ActivitySessionService>();
        services.AddSingleton<IActivitySessionService>(sp =>
            sp.GetRequiredService<ActivitySessionService>());
        services.AddSingleton<SpeechVoicePreferences>();
        services.AddSingleton<VocabularyQuizPreferences>();
        services.AddSingleton<INativePlanCreation, NativePlanCreationAdapter>();
        services.AddSingleton<INativeQuizAttemptCommitter, NativeQuizAttemptCommitterAdapter>();
        services.AddSingleton<INativeQuizSentenceCommitter, NativeQuizSentenceAttemptCommitter>();
        services.AddSingleton<INativeQuizChoices, NativeQuizChoicesAdapter>();
        services.AddSingleton<INativeQuizEligibility, NativeQuizEligibilityAdapter>();
        services.AddSingleton<INativeQuizGrader, NativeQuizGraderAdapter>();
        services.AddSingleton<INativeQuizMedia>(sp =>
            new NativeQuizMedia(
                sp.GetRequiredService<DeviceUserScopeProvider>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<IConnectivityService>(),
                sp.GetRequiredService<IFileSystemService>(),
                sp.GetRequiredService<SpeechVoicePreferences>(),
                sp.GetRequiredService<StreamHistoryRepository>(),
                sp.GetService<IAuthService>(),
                sp.GetService<INativeQuizSpeechSource>(),
                endpoint));

        if (endpoint is not null)
        {
            services.AddSyncServices(database, endpoint, downloadOnlyProfile: true);
            services.AddSingleton<ISyncService, SyncService>();
            services.AddHttpClient("AuthClient", client =>
            {
                client.BaseAddress = endpoint;
                client.Timeout = TimeSpan.FromSeconds(15);
            });
            services.AddSingleton<NativeProfileBootstrap>();
            services.AddSingleton<IdentityAuthService>(sp =>
                new IdentityAuthService(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<ISecureStorageService>(),
                    sp.GetRequiredService<IPreferencesService>(),
                    sp.GetRequiredService<ILogger<IdentityAuthService>>()));
            services.AddSingleton<IAuthService, NativeProfileAuthService>();
            services.AddOwnerBoundNativeAiGateway(endpoint);
            services.AddOwnerBoundNativeQuizSpeech(endpoint);
            services.AddOwnerBoundNativeSentenceGrader(endpoint);
            services.AddOwnerBoundNativeSam(endpoint);
        }

        services.AddSentenceStudioNativeSlice();
        if (endpoint is not null)
            services.AddOwnerBoundNativeSyncReadiness();
        return new NativeServices(services.BuildServiceProvider(validateScopes: true), database);
    }

    /// <summary>
    /// Owner databases migrate (with a verified backup) when their owner signs in;
    /// only the isolated Debug preview database is prepared at startup.
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (!PreviewSchemaEnabled())
            return;
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (PreviewSchemaEnabled())
        {
            var marker = _database + ".preview-schema";
            if (File.Exists(_database) && !File.Exists(marker))
                throw new InvalidOperationException(
                    "Debug preview schema cannot adopt an existing unmarked database.");
            if (!File.Exists(_database))
            {
                if (!await context.Database.EnsureCreatedAsync(cancellationToken))
                    throw new InvalidOperationException(
                        "Debug preview schema did not create the isolated database.");
                File.WriteAllText(marker, "Debug-only EF EnsureCreated; not a migratable production database.");
            }
            _provider.GetRequiredService<ILogger<NativeServices>>().LogWarning(
                "Debug preview uses EF EnsureCreated on an isolated simulator sandbox; migrations are not verified.");
        }
        context.EnableWalMode();
    }

    private static bool PreviewSchemaEnabled()
    {
#if DEBUG
        return typeof(NativeServices).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute =>
                attribute.Key == "SentenceStudio.Native.PreviewSchema"
                && attribute.Value == "true");
#else
        return false;
#endif
    }

    private static string? PreviewSurface()
    {
#if DEBUG
        return typeof(NativeServices).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                attribute.Key == "SentenceStudio.Native.PreviewSurface")?.Value;
#else
        return null;
#endif
    }

    public void Dispose() => _provider.Dispose();

    private sealed class UnavailableLegacyLlmPlan : ILlmPlanGenerationService
    {
        public Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "The legacy AI plan generator is not registered in the native iOS risk slice.");
    }
}
