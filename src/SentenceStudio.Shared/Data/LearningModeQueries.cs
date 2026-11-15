using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

/// <summary>The learner's Learning mode, read from their own profile (docs/known-words-plan.md).</summary>
public static class LearningModeQueries
{
    /// <summary>
    /// The mode on <paramref name="userId"/>'s profile. Learner-led, the default every profile
    /// starts with, when there is no learner or their profile row has not arrived yet.
    /// </summary>
    public static async Task<LearningMode> GetLearningModeAsync(
        this ApplicationDbContext db, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return LearningMode.LearnerLed;
        var mode = await db.UserProfiles.AsNoTracking()
            .Where(profile => profile.Id == userId)
            .Select(profile => (LearningMode?)profile.LearningMode)
            .FirstOrDefaultAsync(ct);
        return mode ?? LearningMode.LearnerLed;
    }
}
