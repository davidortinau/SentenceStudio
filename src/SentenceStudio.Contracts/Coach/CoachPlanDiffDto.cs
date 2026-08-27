namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The difference between two plans.
/// A preview difference shows a possible change. It does not change the plan.
/// </summary>
public sealed class CoachPlanDiffDto
{
    /// <summary>The plan version before the change.</summary>
    public required string BeforePlanVersion { get; init; }

    /// <summary>
    /// The plan version after the change.
    /// A preview uses the preview identifier for this member.
    /// </summary>
    public required string AfterPlanVersion { get; init; }

    /// <summary>True if this difference is a preview. A preview never changes the plan.</summary>
    public required bool IsPreview { get; init; }

    /// <summary>The items, in plan order, with a change kind on each item.</summary>
    public IReadOnlyList<CoachPlanItemDto> Items { get; init; } = Array.Empty<CoachPlanItemDto>();

    /// <summary>The number of new items.</summary>
    public int AddedItemCount { get; init; }

    /// <summary>The number of items the change removes.</summary>
    public int RemovedItemCount { get; init; }

    /// <summary>The number of items the change adjusts.</summary>
    public int AdjustedItemCount { get; init; }

    /// <summary>The number of completed items the server keeps.</summary>
    public int PreservedCompletedItemCount { get; init; }

    /// <summary>The number of started items the server keeps.</summary>
    public int PreservedInProgressItemCount { get; init; }

    /// <summary>The planned minutes before the change.</summary>
    public required int EstimatedMinutesBefore { get; init; }

    /// <summary>The planned minutes after the change.</summary>
    public required int EstimatedMinutesAfter { get; init; }
}
