namespace SentenceStudio.Services.Timer;

/// <summary>
/// Service for tracking time spent on learning activities launched from Today's Plan.
/// Manages timer state, pause/resume, and session persistence.
/// </summary>
public interface IActivityTimerService
{
    /// <summary>
    /// Gets whether a timer session is currently active
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Gets whether the timer is currently running (not paused)
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the elapsed time for the current session
    /// </summary>
    TimeSpan ElapsedTime { get; }

    /// <summary>
    /// Gets the activity type being timed
    /// </summary>
    string? CurrentActivityType { get; }

    /// <summary>
    /// Gets the activity ID (plan item ID) being timed
    /// </summary>
    string? CurrentActivityId { get; }

    /// <summary>
    /// Event fired when timer state changes (started, paused, resumed, stopped)
    /// </summary>
    event EventHandler? TimerStateChanged;

    /// <summary>
    /// Event fired every second while timer is running
    /// </summary>
    event EventHandler<TimeSpan>? TimerTick;

    /// <summary>
    /// Start a new timer session for an activity
    /// </summary>
    /// <param name="activityType">Type of activity being timed</param>
    /// <param name="activityId">Optional ID for specific activity instance</param>
    void StartSession(string activityType, string? activityId = null);

    /// <summary>
    /// Pause the current timer session
    /// </summary>
    void Pause();

    /// <summary>
    /// Resume the paused timer session
    /// </summary>
    void Resume();

    /// <summary>
    /// Stop and save the current timer session
    /// </summary>
    /// <returns>Total elapsed time for the session</returns>
    TimeSpan StopSession();

    /// <summary>
    /// Cancel the current timer session without saving
    /// </summary>
    void CancelSession();
}
