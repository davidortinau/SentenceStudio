using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Runs the source app's fire-and-forget repository sync triggers inside a
/// revocable readiness lease for the published, signed-in device owner.
/// <see cref="OwnerBoundNativeSyncBearerHandler"/> still checks the lease,
/// profile pointer, and JWT owner on every CoreSync send, so a sign-out or
/// owner switch during the run stops further requests. Without a published
/// owner, a matching profile pointer, and a signed-in session, the trigger is
/// skipped and nothing is sent.
/// </summary>
public sealed class OwnerBoundNativeSyncTrigger(
    ISyncService inner,
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    ILogger<OwnerBoundNativeSyncTrigger> logger) : ISyncService
{
    internal ISyncService Inner => inner;

    public bool IsInitialSyncInProgress => inner.IsInitialSyncInProgress;

    public event Action? InitialSyncCompleted
    {
        add => inner.InitialSyncCompleted += value;
        remove => inner.InitialSyncCompleted -= value;
    }

    public Task InitializeDatabaseAsync() => inner.InitializeDatabaseAsync();

    public void BeginInitialSync() => inner.BeginInitialSync();

    public async Task TriggerSyncAsync()
    {
        // A trigger raised inside an in-flight readiness operation already
        // runs under that operation's lease.
        if (scope.TryGetLeasedReadinessOwner(out _))
        {
            await inner.TriggerSyncAsync();
            return;
        }

        var stamp = scope.CaptureStamp();
        var owner = stamp.PublishedOwner;
        if (string.IsNullOrWhiteSpace(owner)
            || !auth.IsSignedIn
            || !string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal))
        {
            logger.LogDebug("Background sync skipped: no verified device owner.");
            return;
        }

        // Refused when the scope changed after the stamp, or when this flow
        // retains a revoked lease; a child flow never adopts a new owner.
        using var lease = scope.TryBeginReadiness(owner, stamp);
        if (lease is null)
        {
            logger.LogDebug("Background sync skipped: the device owner changed.");
            return;
        }

        await inner.TriggerSyncAsync();
    }
}
