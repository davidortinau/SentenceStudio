using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services.Timer;

/// <summary>
/// Implementation of activity timer service using System.Diagnostics.Stopwatch
/// for accurate time tracking independent of system clock changes.
/// Integrates with IProgressService to persist time tracking data.
/// </summary>
public class ActivityTimerService : IActivityTimerService
{
    private readonly Stopwatch _stopwatch = new();
    private System.Timers.Timer? _tickTimer;
    private string? _activityType;
    private string? _activityId;
    private TimeSpan _pausedElapsed = TimeSpan.Zero;
    private int _lastSavedMinutes = 0;
    private readonly Services.Progress.IProgressService? _progressService;
    private readonly ILogger<ActivityTimerService> _logger;

    public bool IsActive => _activityType != null;
    public bool IsRunning => _stopwatch.IsRunning;
    public TimeSpan ElapsedTime => _pausedElapsed + _stopwatch.Elapsed;
    public string? CurrentActivityType => _activityType;
    public string? CurrentActivityId => _activityId;

    public event EventHandler? TimerStateChanged;
    public event EventHandler<TimeSpan>? TimerTick;

    public ActivityTimerService(Services.Progress.IProgressService? progressService = null, ILogger<ActivityTimerService>? logger = null)
    {
        _progressService = progressService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityTimerService>.Instance;

        // Setup tick timer for UI updates (1 second intervals)
        _tickTimer = new System.Timers.Timer(1000);
        _tickTimer.Elapsed += (s, e) => OnTimerTick();
        _tickTimer.AutoReset = true;
    }

    public void StartSession(string activityType, string? activityId = null)
    {
        _logger.LogDebug("⏱️ ActivityTimerService.StartSession - activityType={ActivityType}, activityId={ActivityId}", activityType, activityId);

        // Stop any existing session
        if (IsActive)
        {
            StopSession();
        }

        _activityType = activityType;
        _activityId = activityId;

        // Load existing progress from database to support resume
        _ = LoadExistingProgressAsync();

        _stopwatch.Restart();
        _tickTimer?.Start();

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogDebug("✅ Timer session started");
    }

    public void Pause()
    {
        if (!IsActive || !IsRunning) return;

        _logger.LogDebug("⏱️ Pausing timer - current elapsed: {ElapsedTime}", ElapsedTime);

        _pausedElapsed += _stopwatch.Elapsed;
        _stopwatch.Stop();
        _tickTimer?.Stop();

        // Save progress when pausing
        _ = SaveProgressAsync();

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (!IsActive || IsRunning) return;

        _logger.LogDebug("⏱️ Resuming timer - paused at: {PausedElapsed}", _pausedElapsed);

        _stopwatch.Restart();
        _tickTimer?.Start();

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public TimeSpan StopSession()
    {
        if (!IsActive) return TimeSpan.Zero;

        var totalTime = ElapsedTime;

        _logger.LogDebug("⏱️ Stopping timer session - total time: {TotalTime}", totalTime);

        _stopwatch.Stop();
        _tickTimer?.Stop();

        // DON'T save here - Pause() already saved the progress
        // Saving here would cause double-counting if Pause was called right before Stop
        // The proper flow is: Pause() saves → navigation completes → StopSession() clears state

        // Clear state
        _activityType = null;
        _activityId = null;
        _pausedElapsed = TimeSpan.Zero;
        _lastSavedMinutes = 0;

        TimerStateChanged?.Invoke(this, EventArgs.Empty);

        return totalTime;
    }

    public void CancelSession()
    {
        if (!IsActive) return;

        _logger.LogDebug("⏱️ Canceling timer session");

        _stopwatch.Stop();
        _tickTimer?.Stop();

        _activityType = null;
        _activityId = null;
        _pausedElapsed = TimeSpan.Zero;
        _lastSavedMinutes = 0;

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimerTick()
    {
        if (IsRunning)
        {
            TimerTick?.Invoke(this, ElapsedTime);

            // Auto-save progress every minute
            var currentMinutes = (int)ElapsedTime.TotalMinutes;
            if (currentMinutes > _lastSavedMinutes)
            {
                _ = SaveProgressAsync();
            }
        }
    }

    private async Task SaveProgressAsync()
    {
        _logger.LogDebug("🚀 SaveProgressAsync ENTRY - IsActive={IsActive}, activityId={ActivityId}", IsActive, _activityId);

        if (_progressService == null || string.IsNullOrEmpty(_activityId))
        {
            _logger.LogWarning("❌ Cannot save progress - progressService={ProgressServiceExists}, activityId={ActivityId}", _progressService != null, _activityId);
            return;
        }

        var currentMinutes = (int)ElapsedTime.TotalMinutes;
        _logger.LogDebug("📊 Current elapsed: {ElapsedTime}, minutes={Minutes}, lastSaved={LastSaved}", ElapsedTime, currentMinutes, _lastSavedMinutes);

        if (currentMinutes == _lastSavedMinutes)
        {
            _logger.LogDebug("⏭️ No change in full minutes, skipping save");
            return;
        }

        try
        {
            _logger.LogDebug("💾 Calling UpdatePlanItemProgressAsync('{ActivityId}', {Minutes})", _activityId, currentMinutes);
            await _progressService.UpdatePlanItemProgressAsync(_activityId, currentMinutes);
            _lastSavedMinutes = currentMinutes;
            _logger.LogDebug("✅ Save completed - _lastSavedMinutes updated to {LastSavedMinutes}", _lastSavedMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to save progress");
        }
    }

    private async Task LoadExistingProgressAsync()
    {
        if (_progressService == null || string.IsNullOrEmpty(_activityId))
        {
            _logger.LogWarning("⚠️ Cannot load existing progress - progressService={ProgressServiceExists}, activityId={ActivityId}", _progressService != null, _activityId);
            _pausedElapsed = TimeSpan.Zero;
            _lastSavedMinutes = 0;
            return;
        }

        try
        {
            _logger.LogDebug("📥 Loading existing progress for activity {ActivityId}", _activityId);

            // CRITICAL: Use UTC date to match plan generation (ProgressService uses DateTime.UtcNow.Date)
            var today = DateTime.UtcNow.Date;
            _logger.LogDebug("📅 Query date: {Date:yyyy-MM-dd} (Kind={Kind})", today, today.Kind);

            // ROBUSTNESS FIX: Call GenerateTodaysPlanAsync instead of GetCachedPlanAsync
            // This ensures plan exists even if cache was cleared/expired
            var plan = await _progressService.GenerateTodaysPlanAsync();

            if (plan != null)
            {
                _logger.LogDebug("✅ Plan loaded with {ItemCount} items", plan.Items.Count);
                var planItem = plan.Items.FirstOrDefault(i => i.Id == _activityId);
                if (planItem != null && planItem.MinutesSpent > 0)
                {
                    _pausedElapsed = TimeSpan.FromMinutes(planItem.MinutesSpent);
                    _lastSavedMinutes = planItem.MinutesSpent;
                    _logger.LogDebug("✅ Resumed from {MinutesSpent} minutes (activity: {ActivityTitle})", planItem.MinutesSpent, planItem.TitleKey);
                }
                else if (planItem != null)
                {
                    _pausedElapsed = TimeSpan.Zero;
                    _lastSavedMinutes = 0;
                    _logger.LogDebug("📊 Starting fresh - activity found but MinutesSpent=0 (activity: {ActivityTitle})", planItem.TitleKey);
                }
                else
                {
                    _pausedElapsed = TimeSpan.Zero;
                    _lastSavedMinutes = 0;
                    _logger.LogWarning("⚠️ Activity ID '{ActivityId}' not found in plan items", _activityId);
                    _logger.LogDebug("📊 Available plan item IDs: {ItemIds}", string.Join(", ", plan.Items.Select(i => i.Id)));
                }
            }
            else
            {
                _pausedElapsed = TimeSpan.Zero;
                _lastSavedMinutes = 0;
                _logger.LogWarning("⚠️ No plan found for today");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to load existing progress");
            _pausedElapsed = TimeSpan.Zero;
            _lastSavedMinutes = 0;
        }
    }
}
