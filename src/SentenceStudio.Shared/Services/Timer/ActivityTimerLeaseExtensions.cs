namespace SentenceStudio.Services.Timer;

public static class ActivityTimerLeaseExtensions
{
    public static bool PauseAndStopOwnedSession(
        this IActivityTimerService timer,
        ActivityTimerLease? lease)
    {
        ArgumentNullException.ThrowIfNull(timer);
        if (lease is null)
            return false;

        timer.Pause(lease);
        return timer.StopSession(lease) != TimeSpan.Zero || !timer.IsActive;
    }
}
