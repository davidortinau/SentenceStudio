using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Timer;

namespace SentenceStudio.Native;

/// <summary>
/// Platform heads first register SQLite ApplicationDbContext, secure storage,
/// preferences, connectivity, API HttpClient, CoreSync and existing SentenceStudio
/// repositories/services. This adds only presentation orchestration and safe
/// default ports; it does not initialize or replace device data.
/// </summary>
public static class NativeSliceRegistration
{
    public static IServiceCollection AddSentenceStudioNativeSlice(this IServiceCollection services)
    {
        services.TryAddSingleton<INativeSyncReadiness>(sp =>
            new UnconfirmedNativeSyncReadiness(
                sp.GetRequiredService<IConnectivityService>()));
        services.TryAddSingleton<NativePendingWrites>(sp =>
            new NativePendingWrites().UsePersistentStore(
                sp.GetRequiredService<IPreferencesService>()));
        services.TryAddSingleton<INativeQuizAttemptCommitter, UnavailableNativeQuizAttemptCommitter>();
        services.TryAddSingleton<INativePlanCreation, UnavailableNativePlanCreation>();
        services.TryAddSingleton<INativeQuizChoices, UnavailableNativeQuizChoices>();
        services.TryAddSingleton<INativeQuizEligibility, UnavailableNativeQuizEligibility>();
        services.TryAddSingleton<INativeQuizGrader, UnavailableNativeQuizGrader>();
        services.TryAddSingleton<INativeQuizSentenceGrader, UnavailableNativeQuizSentenceGrader>();
        services.TryAddSingleton<INativeQuizSentenceCommitter, UnavailableNativeQuizSentenceCommitter>();
        services.TryAddSingleton<INativeQuizMedia, UnavailableNativeQuizMedia>();
        services.TryAddScoped<ExampleSentenceRepository>();
        services.TryAddSingleton<VocabularyLookupRepository>();
        services.TryAddSingleton<NativeQuizSettings>();
        services.AddTransient<NativeQuizLaunchService>();
        services.TryAddSingleton<NativeActivityOwner>();
        services.AddTransient<NativeActivityLaunchService>();
        services.AddTransient<NativeActivityTimer>();
        services.AddTransient<NativeMatchingPresenter>();
        services.AddTransient<NativeClozeSentenceSource>();
        services.AddTransient<NativeClozePresenter>();
        services.TryAddSingleton<INativeTranslationAi>(sp => new OwnerBoundNativeTranslationAi(
            sp.GetService<IOwnerBoundNativeAiGateway>(),
            sp.GetService<IOwnerBoundNativeSentenceGateway>()));
        services.AddTransient<NativeTranslationPresenter>();
        // Number Drill: the shared Numbers services the MAUI heads register in CoreServiceExtensions.
        services.TryAddSingleton<SentenceStudio.Services.Numbers.INumberItemGenerator,
            SentenceStudio.Services.Numbers.KoreanNumberItemGenerator>();
        services.TryAddSingleton<SentenceStudio.Services.Numbers.INumberAnswerGrader,
            SentenceStudio.Services.Numbers.KoreanNumberAnswerGrader>();
        services.TryAddScoped<SentenceStudio.Services.Numbers.NumberContentSeeder>();
        services.TryAddScoped<SentenceStudio.Services.Numbers.NumberSessionService>();
        services.AddTransient<NativeNumberDrillPresenter>();
        services.TryAddSingleton<INativeWritingAi, NativeWritingAi>();
        services.AddTransient<NativeWritingPresenter>();
        services.TryAddSingleton<INativeReadingAudioSource, UnavailableNativeReadingAudioSource>();
        services.TryAddSingleton<IAudioPlayerFactory, UnavailableNativeAudioPlayerFactory>();
        services.AddTransient<NativeReadingPresenter>();
        services.AddTransient<NativeShadowingContent>();
        services.TryAddTransient<INativeShadowingSpeech, NativeShadowingSpeech>();
        services.TryAddTransient<INativeShadowingPlayer, UnavailableNativeShadowingPlayer>();
        services.AddTransient<NativeShadowingPresenter>();
        services.AddTransient<NativeHowDoYouSayPresenter>();
        services.TryAddSingleton<INativeWordAssociationAi>(sp => new NativeWordAssociationAi(
            sp.GetService<IOwnerBoundNativeReasoningAiGateway>()));
        services.AddTransient<NativeWordAssociationScores>();
        services.AddTransient<NativeWordAssociationPresenter>();
        services.TryAddSingleton<INativeConversationAi, NativeConversationAi>();
        services.TryAddTransient<INativeConversationScenarios, NativeConversationScenarios>();
        services.AddTransient<NativeConversationPresenter>();
        services.TryAddSingleton<INativeDiaryAi, NativeDiaryAi>();
        services.AddTransient<NativeDiaryStore>();
        services.AddTransient<NativeDiaryPresenter>();
        // Video Watching: the shared YouTube id extraction the source page uses
        // (transient: it resolves the scoped repositories of the activity scope).
        services.TryAddTransient<VideoWatchingService>();
        services.AddTransient<NativeVideoWatchingPresenter>();
        services.AddTransient<NativeFlashcardPresenter>();
        services.TryAddSingleton<INativeSceneAi, NativeSceneAi>();
        services.TryAddSingleton<INativeSceneImageStore, NativeSceneImageStore>();
        services.AddTransient<NativeScenePresenter>();
        services.AddTransient<NativeMinimalPairStore>();
        services.AddTransient<NativeMinimalPairsPresenter>();
        services.TryAddScoped<IPlanCopyProvider, EnglishPlanCopyProvider>();
        services.AddSingleton<NativePlanLeaseFactory>();
        services.TryAddSingleton<INativeProfileRemoteStore>(sp =>
            sp.GetService<IHttpClientFactory>() is { } clients
            && sp.GetService<IAuthService>() is { } profileAuth
                ? new NativeApiProfileRemoteStore(clients, profileAuth,
                    sp.GetRequiredService<ILogger<NativeApiProfileRemoteStore>>())
                : new UnavailableNativeProfileRemoteStore());
        services.TryAddSingleton<INativeOnboardingNames, NativeOnboardingNames>();
        services.AddTransient<NativeAuthPresenter>();
        services.AddTransient<NativeDashboardPresenter>();
        services.AddTransient<NativeQuizPresenter>();
        services.TryAddSingleton<INativeResourceStore, NativeResourceStore>();
        services.AddTransient<NativeResourcePresenter>();
        // Import Content: the shared ContentImportService for the verified owner.
        services.TryAddSingleton<SentenceStudio.Native.Features.Import.INativeContentImport,
            NativeContentImport>();
        // The shared AiService through the owner-bound API gateways (no key on the device), and
        // the YouTube services the source Media Import page runs on the device.
        services.AddNativeSharedAi();
        // YouTube import: the shared YouTube services for the verified owner (channels and the
        // import history through the API when the head registers its owner-bound client).
        services.TryAddSingleton<SentenceStudio.Native.Features.Import.INativeYouTubeImport,
            NativeYouTubeImport>();
        services.TryAddSingleton<INativeSamClient, UnavailableNativeSamClient>();
        services.AddTransient<NativeSamPresenter>();
        return services;
    }

    /// <summary>
    /// Native auth deliberately omits optional automatic sync and profile
    /// backfill dependencies: existing StoreTokens starts an unobservable
    /// fire-and-forget sync and GetAsync backfills unowned rows before owner
    /// verification. The presenter explicitly gates sync and uses GetByIdAsync.
    /// Register this factory instead of injecting optional sync/backfill
    /// dependencies until those shared paths are made owner-safe.
    /// </summary>
    public static IServiceCollection AddSentenceStudioNativeIdentityAuth(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IAuthService>(sp =>
            new IdentityAuthService(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ISecureStorageService>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<ILogger<IdentityAuthService>>(),
                syncService: null,
                dataRecovery: null,
                userProfileRepo: null)));
        return services;
    }

    /// <summary>
    /// The shared timer supplies monotonic elapsed time, not plan writes.
    /// Its legacy progress callback auto-completes at estimated minutes,
    /// which is not the approved quiz completion rule.
    /// </summary>
    internal sealed class NativeQuizClock : IDisposable
    {
        private readonly ActivityTimerService _timer;
        private ActivityTimerLease? _lease;
        private TimeSpan _carried;

        public NativeQuizClock(ILogger<ActivityTimerService> logger) =>
            _timer = new ActivityTimerService(progressService: null, logger);

        public TimeSpan Elapsed => _carried + _timer.ElapsedTime;

        public void Start(string planItemId, TimeSpan carriedElapsed)
        {
            _carried = carriedElapsed;
            _lease = _timer.StartSession("VocabularyReview", planItemId);
        }

        public void Pause()
        {
            if (_lease is not null)
                _timer.Pause(_lease);
        }

        public void Resume()
        {
            if (_lease is not null)
                _timer.Resume(_lease);
        }

        public TimeSpan Stop()
        {
            if (_lease is null)
                return _carried;
            _carried += _timer.StopSession(_lease);
            _lease = null;
            return _carried;
        }

        public void Discard()
        {
            Stop();
            _carried = TimeSpan.Zero;
        }

        public void Dispose() => _timer.Dispose();
    }
}
