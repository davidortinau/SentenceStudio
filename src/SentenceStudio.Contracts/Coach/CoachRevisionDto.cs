namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// One applied coach revision of Today's Plan.
/// A revision record holds no learner text.
/// </summary>
public sealed class CoachRevisionDto
{
    /// <summary>The revision identifier.</summary>
    public required string RevisionId { get; init; }

    /// <summary>The revision number in this session. The first revision is 1.</summary>
    public required int RevisionNumber { get; init; }

    /// <summary>What caused this revision.</summary>
    public required CoachRevisionSource Source { get; init; }

    /// <summary>The constraint fields this revision changed.</summary>
    public IReadOnlyList<CoachConstraintField> ChangedFields { get; init; } = Array.Empty<CoachConstraintField>();

    /// <summary>The localized summary of this revision.</summary>
    public required string Summary { get; init; }

    /// <summary>The plan version before this revision.</summary>
    public required string BeforePlanVersion { get; init; }

    /// <summary>The plan version after this revision.</summary>
    public required string AfterPlanVersion { get; init; }

    /// <summary>The time the server applied this revision.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>True if a later undo reversed this revision.</summary>
    public bool IsUndone { get; init; }

    /// <summary>True if the learner can undo this revision now.</summary>
    public bool CanUndo { get; init; }
}
