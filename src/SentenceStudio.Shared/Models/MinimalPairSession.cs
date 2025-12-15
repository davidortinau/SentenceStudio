namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents one session/run of Minimal Pairs practice.
/// Tracks session-level metadata like mode, duration, and timing.
/// </summary>
public class MinimalPairSession
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// User who performed this session (default 1 for single-user app)
    /// </summary>
    public int UserId { get; set; } = 1;

    /// <summary>
    /// Practice mode: "Focus" (single pair), "Mixed" (multiple pairs), "Adaptive"
    /// </summary>
    public string Mode { get; set; } = "Focus";

    /// <summary>
    /// Planned number of trials for this session (optional, may be null for open-ended sessions)
    /// </summary>
    public int? PlannedTrialCount { get; set; }

    /// <summary>
    /// When the session started
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the session ended (null until session is completed)
    /// </summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Creation timestamp (typically same as StartedAt)
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation property
    public ICollection<MinimalPairAttempt> Attempts { get; set; } = new List<MinimalPairAttempt>();
}
