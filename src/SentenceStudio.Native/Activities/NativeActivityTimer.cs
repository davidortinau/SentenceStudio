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
            || NativeActivityCatalog.ForRoute(launch.Route)?.TimerType is not { } type)
            return false;

        // Vocabulary is not part of the timer context: activities choose their
        // words after start, as the source pages do.
        var lease = await _timer.StartValidatedSessionAsync(new ActivityTimerStartRequest(
            launch.UserId, type, launch.PlanItemId, launch.TimerResourceId,
            launch.SkillId, VocabularyWordIds: null), cancellationToken);
        if (lease?.ActivityId is null || !owner.IsActive(launch.UserId))
        {
            if (lease is not null)
                _timer.CancelSession(lease);
            logger.LogWarning("Native activity timer refused an unowned or stale launch.");
            return false;
        }
        _lease = lease;
        if (!launch.IsPlanItem)
            pendingWrites.MarkPending(launch.UserId);
        return true;
    }

    /// <summary>Stops the timer and saves its whole minutes. Returns the saved minutes, or null when nothing was saved.</summary>
    public async Task<int?> StopAsync(CancellationToken cancellationToken = default)
    {
        var lease = _lease;
        _lease = null;
        if (lease?.ActivityId is null)
            return null;
        var minutes = (int)Math.Floor(_timer.StopSession(lease).TotalMinutes);
        if (!owner.IsActive(lease.UserId))
        {
            logger.LogWarning("Native activity timer did not save minutes after the owner changed.");
            return null;
        }
        if (!await progress.UpdatePlanItemProgressAsync(
                lease.UserId, lease.ActivityId, minutes, cancellationToken))
            return null;
        pendingWrites.MarkPending(lease.UserId);
        return minutes;
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
