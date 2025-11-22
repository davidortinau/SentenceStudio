using System.Diagnostics;

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

    public bool IsActive => _activityType != null;
    public bool IsRunning => _stopwatch.IsRunning;
    public TimeSpan ElapsedTime => _pausedElapsed + _stopwatch.Elapsed;
    public string? CurrentActivityType => _activityType;
    public string? CurrentActivityId => _activityId;

    public event EventHandler? TimerStateChanged;
    public event EventHandler<TimeSpan>? TimerTick;

    public ActivityTimerService(Services.Progress.IProgressService? progressService = null)
    {
        _progressService = progressService;

        // Setup tick timer for UI updates (1 second intervals)
        _tickTimer = new System.Timers.Timer(1000);
        _tickTimer.Elapsed += (s, e) => OnTimerTick();
        _tickTimer.AutoReset = true;
    }

    public void StartSession(string activityType, string? activityId = null)
    {
        System.Diagnostics.Debug.WriteLine($"⏱️ ActivityTimerService.StartSession - activityType={activityType}, activityId={activityId}");

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
        System.Diagnostics.Debug.WriteLine($"✅ Timer session started");
    }

    public void Pause()
    {
        if (!IsActive || !IsRunning) return;

        System.Diagnostics.Debug.WriteLine($"⏱️ Pausing timer - current elapsed: {ElapsedTime}");

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

        System.Diagnostics.Debug.WriteLine($"⏱️ Resuming timer - paused at: {_pausedElapsed}");

        _stopwatch.Restart();
        _tickTimer?.Start();

        TimerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public TimeSpan StopSession()
    {
        if (!IsActive) return TimeSpan.Zero;

        var totalTime = ElapsedTime;

        System.Diagnostics.Debug.WriteLine($"⏱️ Stopping timer session - total time: {totalTime}");

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

        System.Diagnostics.Debug.WriteLine($"⏱️ Canceling timer session");

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
        System.Diagnostics.Debug.WriteLine($"🚀 SaveProgressAsync ENTRY - IsActive={IsActive}, activityId={_activityId}");

        if (_progressService == null || string.IsNullOrEmpty(_activityId))
        {
            System.Diagnostics.Debug.WriteLine($"❌ Cannot save progress - progressService={(_progressService != null)}, activityId={_activityId}");
            return;
        }

        var currentMinutes = (int)ElapsedTime.TotalMinutes;
        System.Diagnostics.Debug.WriteLine($"📊 Current elapsed: {ElapsedTime}, minutes={currentMinutes}, lastSaved={_lastSavedMinutes}");

        if (currentMinutes == _lastSavedMinutes)
        {
            System.Diagnostics.Debug.WriteLine($"⏭️ No change in full minutes, skipping save");
            return;
        }

        try
        {
            System.Diagnostics.Debug.WriteLine($"💾 Calling UpdatePlanItemProgressAsync('{_activityId}', {currentMinutes})");
            await _progressService.UpdatePlanItemProgressAsync(_activityId, currentMinutes);
            _lastSavedMinutes = currentMinutes;
            System.Diagnostics.Debug.WriteLine($"✅ Save completed - _lastSavedMinutes updated to {_lastSavedMinutes}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Failed to save progress: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"❌ Stack trace: {ex.StackTrace}");
        }
    }

    private async Task LoadExistingProgressAsync()
    {
        if (_progressService == null || string.IsNullOrEmpty(_activityId))
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Cannot load existing progress - progressService={(_progressService != null)}, activityId={_activityId}");
            _pausedElapsed = TimeSpan.Zero;
            _lastSavedMinutes = 0;
            return;
        }

        try
        {
            System.Diagnostics.Debug.WriteLine($"📥 Loading existing progress for activity {_activityId}");

            // CRITICAL: Use UTC date to match plan generation (ProgressService uses DateTime.UtcNow.Date)
            var today = DateTime.UtcNow.Date;
            System.Diagnostics.Debug.WriteLine($"📅 Query date: {today:yyyy-MM-dd} (Kind={today.Kind})");

            // ROBUSTNESS FIX: Call GenerateTodaysPlanAsync instead of GetCachedPlanAsync
            // This ensures plan exists even if cache was cleared/expired
            var plan = await _progressService.GenerateTodaysPlanAsync();

            if (plan != null)
            {
                System.Diagnostics.Debug.WriteLine($"✅ Plan loaded with {plan.Items.Count} items");
                var planItem = plan.Items.FirstOrDefault(i => i.Id == _activityId);
                if (planItem != null && planItem.MinutesSpent > 0)
                {
                    _pausedElapsed = TimeSpan.FromMinutes(planItem.MinutesSpent);
                    _lastSavedMinutes = planItem.MinutesSpent;
                    System.Diagnostics.Debug.WriteLine($"✅ Resumed from {planItem.MinutesSpent} minutes (activity: {planItem.TitleKey})");
                }
                else if (planItem != null)
                {
                    _pausedElapsed = TimeSpan.Zero;
                    _lastSavedMinutes = 0;
                    System.Diagnostics.Debug.WriteLine($"📊 Starting fresh - activity found but MinutesSpent=0 (activity: {planItem.TitleKey})");
                }
                else
                {
                    _pausedElapsed = TimeSpan.Zero;
                    _lastSavedMinutes = 0;
                    System.Diagnostics.Debug.WriteLine($"⚠️ Activity ID '{_activityId}' not found in plan items");
                    System.Diagnostics.Debug.WriteLine($"📊 Available plan item IDs: {string.Join(", ", plan.Items.Select(i => i.Id))}");
                }
            }
            else
            {
                _pausedElapsed = TimeSpan.Zero;
                _lastSavedMinutes = 0;
                System.Diagnostics.Debug.WriteLine($"⚠️ No plan found for today");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ Failed to load existing progress: {ex.Message}");
            _pausedElapsed = TimeSpan.Zero;
            _lastSavedMinutes = 0;
        }
    }
}
