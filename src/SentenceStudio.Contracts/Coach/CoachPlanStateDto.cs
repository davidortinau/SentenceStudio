namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The plan canvas state for one coach session.
/// </summary>
public sealed class CoachPlanStateDto
{
    /// <summary>The user-local date of this plan.</summary>
    public required DateOnly PlanDate { get; init; }

    /// <summary>
    /// The plan version. Send this value back in a turn request.
    /// The server rejects a request that carries an old version.
    /// </summary>
    public required string PlanVersion { get; init; }

    /// <summary>The plan items in plan order.</summary>
    public IReadOnlyList<CoachPlanItemDto> Items { get; init; } = Array.Empty<CoachPlanItemDto>();

    /// <summary>The constraints the server applied to this plan.</summary>
    public required CoachConstraintSetDto AppliedConstraints { get; init; }

    /// <summary>The planned minutes for the full plan.</summary>
    public required int EstimatedTotalMinutes { get; init; }

    /// <summary>The number of completed items.</summary>
    public required int CompletedCount { get; init; }

    /// <summary>The number of items in the plan.</summary>
    public required int TotalCount { get; init; }

    /// <summary>The completion percentage. The range is 0 to 100.</summary>
    public required double CompletionPercentage { get; init; }

    /// <summary>The last coach revision of this plan. Null if the coach made no change.</summary>
    public CoachRevisionDto? LastRevision { get; init; }

    /// <summary>True if the learner can undo the last coach revision.</summary>
    public bool CanUndo { get; init; }
}
