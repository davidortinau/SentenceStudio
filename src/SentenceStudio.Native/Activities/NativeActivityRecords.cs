using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Builds the UserActivity row an activity writes after an answer, grade or
/// lookup. CreatedAt/UpdatedAt are UTC instants: ProgressService.ComputeStreak
/// and the server convert them to the learner's local day, so a local
/// DateTime.Now would move a UTC+9 evening session onto the next day. The row
/// is always stamped with the explicit owner; an absent owner throws instead
/// of letting UserActivityRepository fall back to an ambient profile.
/// </summary>
public static class NativeUserActivity
{
    public static UserActivity Create(
        string owner, Activity activity, string? input, double accuracy,
        double fluency = 0, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return new UserActivity
        {
            Activity = activity.ToString(),
            Input = input,
            Accuracy = accuracy,
            Fluency = fluency,
            UserProfileId = owner,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
