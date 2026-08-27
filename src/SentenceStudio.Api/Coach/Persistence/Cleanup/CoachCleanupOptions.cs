using System.ComponentModel.DataAnnotations;

namespace SentenceStudio.Api.Coach.Persistence.Cleanup;

/// <summary>
/// Schedule settings for the coach retention job. Bound from <c>Coach:Cleanup:*</c>.
/// </summary>
public sealed class CoachCleanupOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Coach:Cleanup";

    /// <summary>
    /// Whether the background job runs. Default true, but the host also refuses to start it when
    /// the coach feature itself is off or when the environment is Testing, so enabling it here
    /// can never make a test host start deleting rows on a timer.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to wait between passes. Fifteen minutes is short enough that expired checkpoints
    /// do not accumulate for long, and long enough that the advisory lock is uncontended in the
    /// normal case.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long to wait after a failed pass before retrying. Shorter than
    /// <see cref="Interval"/> so a transient database blip does not cost a full cycle, but not so
    /// short that a persistent failure becomes a hot loop against a struggling database.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Delay before the first pass, so cleanup does not compete with migration and warm-up on a
    /// cold start, and so replicas starting together do not all reach the lock in the same
    /// instant.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(2);
}
