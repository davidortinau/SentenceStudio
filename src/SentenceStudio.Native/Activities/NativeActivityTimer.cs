using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;

namespace SentenceStudio.Native;

/// <summary>
/// Source activity timing (activities.md 1.3) through the shared
/// <see cref="ActivityTimerService"/>, always with an explicit owner:
/// a plan launch validates the plan row and resumes its saved minutes; a
/// Choose My Own launch creates an owner-validated ad-hoc row. The shared
/// service saves whole minutes while running and completes the row when
/// minutes reach its estimate. <see cref="StopAsync"/> awaits the final save
/// so the Dashboard reload after exit shows it.
/// </summary>
public sealed class NativeActivityTimer(
    IProgressService progress,
    NativeActivityOwner owner,
    NativePendingWrites pendingWrites,
    ILogger<ActivityTimerService> timerLogger,
    ILogger<NativeActivityTimer> logger) : IDisposable
{
    private readonly ActivityTimerService _timer = new(progress, timerLogger);
    private ActivityTimerLease? _lease;
    private bool _disposed;

    public bool IsRunning => _lease is not null;

    /// <summary>The DailyPlanCompletion PlanItemId being timed (plan or adhoc-*).</summary>
    public string? RowPlanItemId => _lease?.ActivityId;

    public TimeSpan Elapsed => _timer.ElapsedTime;

    public async Task<bool> StartAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lease is not null)
            return owner.IsActive(_lease.UserId) && _lease.UserId == launch.UserId;
        if (!owner.IsActive(launch.UserId)
            || NativeActivityCatalog.ForRoute(launch.Route) is not { } definition
            || (launch.ActivityType ?? definition.TimerType) is not { } type
            || !definition.PlanTypes.Contains(type))
            return false;

        // Vocabulary is not part of the timer context: activities choose their
        // words after start, as the source pages do.
        var lease = await _timer.StartValidatedSessionAsync(new ActivityTimerStartRequest(
            launch.UserId, type, launch.PlanItemId, launch.TimerResourceId,
            definition.TimerRecordsSkill ? launch.SkillId : null,
            VocabularyWordIds: null), cancellationToken);
        if (lease?.ActivityId is null || _disposed || !owner.IsActive(launch.UserId))
        {
            if (lease is not null)
            {
                _timer.CancelSession(lease);
                // A refused Choose My Own start must not leave an empty Freeform row behind.
                if (!launch.IsPlanItem && lease.ActivityId is { } adHocId)
                    await DiscardAdHocAsync(launch.UserId, adHocId);
            }
            logger.LogWarning("Native activity timer refused an unowned or stale launch.");
            return false;
        }
        _lease = lease;
        if (!launch.IsPlanItem)
            pendingWrites.MarkPending(launch.UserId);
        return true;
    }

    /// <summary>
    /// Stops the timer and saves its whole minutes. <see cref="NativeActivityStop.Failed"/>
    /// means a running session's minutes could not be saved (row gone, day changed,
    /// or owner changed), so the screen can say so instead of losing them silently.
    /// </summary>
    public async Task<NativeActivityStop> StopAsync(CancellationToken cancellationToken = default)
    {
        var lease = _lease;
        _lease = null;
        if (lease?.ActivityId is null)
            return NativeActivityStop.NotRunning;
        var minutes = (int)Math.Floor(_timer.StopSession(lease).TotalMinutes);
        if (!owner.IsActive(lease.UserId))
        {
            logger.LogWarning("Native activity timer did not save minutes after the owner changed.");
            return NativeActivityStop.Failed;
        }
        if (!await progress.UpdatePlanItemProgressAsync(
                lease.UserId, lease.ActivityId, minutes, cancellationToken))
        {
            logger.LogWarning("Native activity timer minutes were refused by the plan row.");
            return NativeActivityStop.Failed;
        }
        pendingWrites.MarkPending(lease.UserId);
        return NativeActivityStop.Saved;
    }

    private async Task DiscardAdHocAsync(string userId, string adHocId)
    {
        try
        {
            await progress.DiscardAdHocSessionAsync(userId, adHocId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native activity timer could not discard a refused ad-hoc row.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Navigation away without Back: the source DisposeAsync pauses (which
        // saves a changed whole minute through the shared service) and stops.
        if (_lease is { } lease && owner.IsActive(lease.UserId))
            _timer.PauseAndStopOwnedSession(lease);
        _lease = null;
        _timer.Dispose();
    }
}

public enum NativeActivityStop { NotRunning, Saved, Failed }
