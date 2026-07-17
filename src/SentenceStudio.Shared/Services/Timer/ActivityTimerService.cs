using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Services.Timer;

/// <summary>
/// Circuit-local activity timer. Every start receives a monotonically increasing
/// generation and an immutable lease so stale continuations and older components
/// cannot mutate or stop a newer session.
/// </summary>
public class ActivityTimerService : IActivityTimerService, IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly System.Timers.Timer _tickTimer;
    private readonly IProgressService? _progressService;
    private readonly ILogger<ActivityTimerService> _logger;

    private ActivityTimerLease? _currentLease;
    private CancellationTokenSource? _startCts;
    private long _generation;
    private string? _activityType;
    private string? _activityId;
    private TimeSpan _pausedElapsed;
    private int _lastSavedMinutes;
    private int _lastSaveRequestedMinutes;
    private bool _previousProgressLoaded;
    private bool _disposed;

    public ActivityTimerService(
        IProgressService? progressService = null,
        ILogger<ActivityTimerService>? logger = null)
    {
        _progressService = progressService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityTimerService>.Instance;
        _tickTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _tickTimer.Elapsed += OnTickTimerElapsed;
    }

    public bool IsActive
    {
        get { lock (_gate) return _currentLease is not null; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _stopwatch.IsRunning; }
    }

    public TimeSpan ElapsedTime
    {
        get
        {
            lock (_gate)
                return GetElapsedTimeLocked();
        }
    }

    public string? CurrentActivityType
    {
        get { lock (_gate) return _activityType; }
    }

    public string? CurrentActivityId
    {
        get { lock (_gate) return _activityId; }
    }

    public event EventHandler? TimerStateChanged;
    public event EventHandler<TimeSpan>? TimerTick;

    public ActivityTimerLease StartSession(
        string activityType,
        string? activityId = null,
        string? resourceId = null,
        string? skillId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var start = BeginSession(string.Empty, activityType, activityId);
        if (string.IsNullOrWhiteSpace(activityId))
        {
            _ = StartLegacyAdHocAsync(
                start.Lease,
                resourceId,
                skillId,
                start.Token);
        }
        else
        {
            _ = LoadLegacyPlanProgressAsync(start.Lease, start.Token);
        }

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
        return start.Lease;
    }

    public async Task<ActivityTimerLease?> StartValidatedSessionAsync(
        ActivityTimerStartRequest request,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var userId = Normalize(request.UserId);
        if (userId is null)
        {
            _logger.LogWarning("Validated timer start refused because no explicit user was supplied.");
            return null;
        }

        var activityType = request.ActivityType.ToString();
        var start = BeginSession(userId, activityType, Normalize(request.PlanItemId));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, start.Token);
        TimerStateChanged?.Invoke(this, EventArgs.Empty);

        if (start.Lease.ActivityId is not null)
        {
            return await StartValidatedPlanItemAsync(
                start.Lease,
                request,
                linkedCts.Token);
        }

        return await StartValidatedAdHocAsync(
            start.Lease,
            request,
            linkedCts.Token);
    }

    public void Pause() => PauseInternal(null);

    public bool Pause(ActivityTimerLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return PauseInternal(lease);
    }

    public void Resume() => ResumeInternal(null);

    public bool Resume(ActivityTimerLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ResumeInternal(lease);
    }

    public TimeSpan StopSession() => StopSessionInternal(null);

    public TimeSpan StopSession(ActivityTimerLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return StopSessionInternal(lease);
    }

    public void CancelSession() => CancelSessionInternal(null);

    public bool CancelSession(ActivityTimerLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return CancelSessionInternal(lease);
    }

    private SessionStart BeginSession(string userId, string activityType, string? activityId)
    {
        SaveRequest? previousSave;
        ActivityTimerLease lease;
        CancellationToken token;

        lock (_gate)
        {
            previousSave = CaptureCurrentSaveLocked();
            ClearCurrentLocked(invalidateGeneration: true);

            _generation++;
            _startCts = new CancellationTokenSource();
            token = _startCts.Token;
            lease = new ActivityTimerLease(
                Guid.NewGuid(),
                _generation,
                userId,
                activityType,
                activityId);
            _currentLease = lease;
            _activityType = activityType;
            _activityId = activityId;
            _pausedElapsed = TimeSpan.Zero;
            _lastSavedMinutes = 0;
            _lastSaveRequestedMinutes = 0;
            _previousProgressLoaded = false;
        }

        if (previousSave is not null)
            _ = SaveProgressAsync(previousSave);

        return new SessionStart(lease, token);
    }

    private async Task<ActivityTimerLease?> StartValidatedPlanItemAsync(
        ActivityTimerLease lease,
        ActivityTimerStartRequest request,
        CancellationToken ct)
    {
        if (_progressService is null || lease.ActivityId is null)
        {
            CancelIfOwned(lease);
            return null;
        }

        try
        {
            var validated = await _progressService.ValidatePlanItemAsync(
                lease.UserId,
                lease.ActivityId,
                request.ActivityType,
                request.ResourceId,
                request.SkillId,
                request.VocabularyWordIds,
                ct);
            if (ct.IsCancellationRequested)
            {
                CancelIfOwned(lease);
                return null;
            }

            if (validated is null)
            {
                CancelIfOwned(lease);
                return null;
            }

            return TryAcceptStart(
                lease,
                validated.PlanItemId,
                TimeSpan.FromMinutes(validated.MinutesSpent),
                validated.MinutesSpent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelIfOwned(lease);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validated current-plan timer start failed.");
            CancelIfOwned(lease);
            return null;
        }
    }

    private async Task<ActivityTimerLease?> StartValidatedAdHocAsync(
        ActivityTimerLease lease,
        ActivityTimerStartRequest request,
        CancellationToken ct)
    {
        if (_progressService is null)
        {
            CancelIfOwned(lease);
            return null;
        }

        string? persistedId = null;
        try
        {
            persistedId = await _progressService.StartAdHocSessionAsync(
                lease.UserId,
                request.ActivityType,
                request.ResourceId,
                request.SkillId,
                request.VocabularyWordIds,
                ct: ct);
            if (ct.IsCancellationRequested)
            {
                if (!string.IsNullOrWhiteSpace(persistedId))
                    await DiscardStaleAdHocAsync(lease.UserId, persistedId);
                CancelIfOwned(lease);
                return null;
            }

            if (string.IsNullOrWhiteSpace(persistedId))
            {
                CancelIfOwned(lease);
                return null;
            }

            var accepted = TryAcceptStart(
                lease,
                persistedId,
                TimeSpan.Zero,
                lastSavedMinutes: 0);
            if (accepted is not null)
                return accepted;

            await DiscardStaleAdHocAsync(lease.UserId, persistedId);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(persistedId))
                await DiscardStaleAdHocAsync(lease.UserId, persistedId);
            CancelIfOwned(lease);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validated ad-hoc timer start failed.");
            if (!string.IsNullOrWhiteSpace(persistedId))
                await DiscardStaleAdHocAsync(lease.UserId, persistedId);
            CancelIfOwned(lease);
            return null;
        }
    }

    private async Task StartLegacyAdHocAsync(
        ActivityTimerLease lease,
        string? resourceId,
        string? skillId,
        CancellationToken ct)
    {
        if (_progressService is null
            || !Enum.TryParse<PlanActivityType>(lease.ActivityType, out var activityType))
        {
            TryAcceptStart(lease, activityId: null, TimeSpan.Zero, lastSavedMinutes: 0);
            return;
        }

        try
        {
            var persistedId = await _progressService.StartAdHocSessionAsync(
                activityType,
                resourceId,
                skillId,
                ct: ct);
            if (string.IsNullOrWhiteSpace(persistedId))
            {
                CancelIfOwned(lease);
                return;
            }

            if (TryAcceptStart(lease, persistedId, TimeSpan.Zero, lastSavedMinutes: 0) is null)
            {
                var activeUserId = lease.UserId;
                if (!string.IsNullOrWhiteSpace(activeUserId))
                    await DiscardStaleAdHocAsync(activeUserId, persistedId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelIfOwned(lease);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Legacy ad-hoc timer persistence failed; timer will run without persistence.");
            TryAcceptStart(lease, activityId: null, TimeSpan.Zero, lastSavedMinutes: 0);
        }
    }

    private async Task LoadLegacyPlanProgressAsync(
        ActivityTimerLease lease,
        CancellationToken ct)
    {
        var pausedElapsed = TimeSpan.Zero;
        var lastSavedMinutes = 0;

        if (_progressService is not null && lease.ActivityId is not null)
        {
            try
            {
                var plan = await _progressService.GenerateTodaysPlanAsync(ct);
                var planItem = plan.Items.FirstOrDefault(item => item.Id == lease.ActivityId);
                if (planItem is not null)
                {
                    pausedElapsed = TimeSpan.FromMinutes(planItem.MinutesSpent);
                    lastSavedMinutes = planItem.MinutesSpent;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancelIfOwned(lease);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy timer progress load failed.");
            }
        }

        TryAcceptStart(lease, lease.ActivityId, pausedElapsed, lastSavedMinutes);
    }

    private ActivityTimerLease? TryAcceptStart(
        ActivityTimerLease lease,
        string? activityId,
        TimeSpan pausedElapsed,
        int lastSavedMinutes)
    {
        ActivityTimerLease accepted;
        lock (_gate)
        {
            if (_disposed || !OwnsCurrentLocked(lease))
                return null;

            accepted = lease with { ActivityId = activityId };
            _currentLease = accepted;
            _activityId = activityId;
            _pausedElapsed = pausedElapsed;
            _lastSavedMinutes = lastSavedMinutes;
            _lastSaveRequestedMinutes = lastSavedMinutes;
            _previousProgressLoaded = true;
            _stopwatch.Restart();
            _tickTimer.Start();
        }

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
        return accepted;
    }

    private bool PauseInternal(ActivityTimerLease? owner)
    {
        SaveRequest? save;
        lock (_gate)
        {
            if (_disposed
                || _currentLease is null
                || (owner is not null && !OwnsCurrentLocked(owner))
                || !_stopwatch.IsRunning)
            {
                return false;
            }

            _pausedElapsed += _stopwatch.Elapsed;
            _stopwatch.Reset();
            _tickTimer.Stop();
            save = CaptureCurrentSaveLocked();
        }

        if (save is not null)
            _ = SaveProgressAsync(save);
        TimerStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool ResumeInternal(ActivityTimerLease? owner)
    {
        lock (_gate)
        {
            if (_disposed
                || _currentLease is null
                || (owner is not null && !OwnsCurrentLocked(owner))
                || _stopwatch.IsRunning
                || !_previousProgressLoaded)
            {
                return false;
            }

            _stopwatch.Restart();
            _tickTimer.Start();
        }

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private TimeSpan StopSessionInternal(ActivityTimerLease? owner)
    {
        TimeSpan elapsed;
        lock (_gate)
        {
            if (_disposed
                || _currentLease is null
                || (owner is not null && !OwnsCurrentLocked(owner)))
            {
                return TimeSpan.Zero;
            }

            elapsed = GetElapsedTimeLocked();
            ClearCurrentLocked(invalidateGeneration: true);
        }

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
        return elapsed;
    }

    private bool CancelSessionInternal(ActivityTimerLease? owner)
    {
        lock (_gate)
        {
            if (_disposed
                || _currentLease is null
                || (owner is not null && !OwnsCurrentLocked(owner)))
            {
                return false;
            }

            ClearCurrentLocked(invalidateGeneration: true);
        }

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void CancelIfOwned(ActivityTimerLease lease)
    {
        CancelSessionInternal(lease);
    }

    private void ClearCurrentLocked(bool invalidateGeneration)
    {
        _startCts?.Cancel();
        _startCts?.Dispose();
        _startCts = null;
        _stopwatch.Reset();
        if (!_disposed)
            _tickTimer.Stop();
        _currentLease = null;
        _activityType = null;
        _activityId = null;
        _pausedElapsed = TimeSpan.Zero;
        _lastSavedMinutes = 0;
        _lastSaveRequestedMinutes = 0;
        _previousProgressLoaded = false;
        if (invalidateGeneration)
            _generation++;
    }

    private SaveRequest? CaptureCurrentSaveLocked()
    {
        if (_currentLease is null
            || !_previousProgressLoaded
            || string.IsNullOrWhiteSpace(_activityId))
        {
            return null;
        }

        var currentMinutes = (int)GetElapsedTimeLocked().TotalMinutes;
        if (currentMinutes == _lastSavedMinutes
            || currentMinutes == _lastSaveRequestedMinutes)
        {
            return null;
        }

        _lastSaveRequestedMinutes = currentMinutes;
        return new SaveRequest(_currentLease, _activityId, currentMinutes);
    }

    private async Task SaveProgressAsync(SaveRequest request)
    {
        if (_progressService is null)
            return;

        try
        {
            var saved = string.IsNullOrWhiteSpace(request.Lease.UserId)
                ? await SaveLegacyProgressAsync(request)
                : await _progressService.UpdatePlanItemProgressAsync(
                    request.Lease.UserId,
                    request.ActivityId,
                    request.Minutes);

            if (!saved)
                return;

            lock (_gate)
            {
                if (OwnsCurrentLocked(request.Lease))
                {
                    _lastSavedMinutes = request.Minutes;
                    _lastSaveRequestedMinutes = request.Minutes;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (OwnsCurrentLocked(request.Lease))
                    _lastSaveRequestedMinutes = _lastSavedMinutes;
            }
            _logger.LogError(ex, "Failed to save activity timer progress.");
        }
    }

    private async Task<bool> SaveLegacyProgressAsync(SaveRequest request)
    {
        await _progressService!.UpdatePlanItemProgressAsync(
            request.ActivityId,
            request.Minutes);
        return true;
    }

    private async Task DiscardStaleAdHocAsync(string userId, string activityId)
    {
        if (_progressService is null || string.IsNullOrWhiteSpace(userId))
            return;

        try
        {
            await _progressService.DiscardAdHocSessionAsync(
                userId,
                activityId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discard a stale unaccepted ad-hoc timer session.");
        }
    }

    private void OnTickTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e) => OnTimerTick();

    private void OnTimerTick()
    {
        TimeSpan elapsed;
        SaveRequest? save;
        lock (_gate)
        {
            if (_disposed || _currentLease is null || !_stopwatch.IsRunning)
                return;

            elapsed = GetElapsedTimeLocked();
            save = CaptureCurrentSaveLocked();
        }

        TimerTick?.Invoke(this, elapsed);
        if (save is not null)
            _ = SaveProgressAsync(save);
    }

    private bool OwnsCurrentLocked(ActivityTimerLease lease)
    {
        return _currentLease is not null
            && _currentLease.SessionId == lease.SessionId
            && _currentLease.Generation == lease.Generation
            && string.Equals(_currentLease.UserId, lease.UserId, StringComparison.Ordinal)
            && string.Equals(_currentLease.ActivityType, lease.ActivityType, StringComparison.Ordinal);
    }

    private TimeSpan GetElapsedTimeLocked() =>
        _pausedElapsed + (_stopwatch.IsRunning ? _stopwatch.Elapsed : TimeSpan.Zero);

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            ClearCurrentLocked(invalidateGeneration: true);
            _disposed = true;
            _tickTimer.Elapsed -= OnTickTimerElapsed;
            _tickTimer.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private sealed record SessionStart(
        ActivityTimerLease Lease,
        CancellationToken Token);

    private sealed record SaveRequest(
        ActivityTimerLease Lease,
        string ActivityId,
        int Minutes);
}
