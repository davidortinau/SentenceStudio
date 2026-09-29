using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
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
        services.TryAddSingleton<INativeQuizMedia, UnavailableNativeQuizMedia>();
        services.TryAddScoped<IPlanCopyProvider, EnglishPlanCopyProvider>();
        services.AddSingleton<NativePlanLeaseFactory>();
        services.AddTransient<NativeAuthPresenter>();
        services.AddTransient<NativeDashboardPresenter>();
        services.AddTransient<NativeQuizPresenter>();
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
