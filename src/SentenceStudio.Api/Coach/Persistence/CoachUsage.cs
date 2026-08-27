namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// Per-learner, per-user-local-day coach usage counters. Daily and weekly run, token,
/// and estimated-cost limits are enforced from these rows.
/// </summary>
public sealed class CoachUsage
{
    /// <summary>Application-owned identifier. EF never generates this value.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Owning learner. Required and indexed. Every store query filters on it.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>The learner-local date these counters cover.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>
    /// The ISO week key for <see cref="LocalDate"/> in <c>yyyy-Www</c> form, so weekly
    /// limits are a single indexed equality filter instead of a date range scan.
    /// </summary>
    public string WeekKey { get; set; } = string.Empty;

    /// <summary>Completed coach runs for the day.</summary>
    public int RunCount { get; set; }

    /// <summary>Prompt tokens consumed for the day.</summary>
    public long InputTokens { get; set; }

    /// <summary>Completion tokens consumed for the day.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Estimated cost in USD for the day.</summary>
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>When the row was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the row was last written (UTC).</summary>
    public DateTime UpdatedAt { get; set; }
}
