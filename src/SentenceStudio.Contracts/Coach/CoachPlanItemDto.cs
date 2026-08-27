namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// One item in a coach plan view. The server localizes every text member.
/// This item carries no identity data and no learning answer content.
/// Clients start activities from Today's Plan, not from this view.
/// </summary>
public sealed class CoachPlanItemDto
{
    /// <summary>The stable plan item identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The activity type.</summary>
    public required CoachPlanActivityType ActivityType { get; init; }

    /// <summary>The localized title.</summary>
    public required string Title { get; init; }

    /// <summary>The localized description.</summary>
    public required string Description { get; init; }

    /// <summary>The order of this item in the plan. A lower number comes first.</summary>
    public required int Priority { get; init; }

    /// <summary>The planned minutes for this item.</summary>
    public required int EstimatedMinutes { get; init; }

    /// <summary>The minutes the learner spent on this item.</summary>
    public int MinutesSpent { get; init; }

    /// <summary>True if the learner completed this item.</summary>
    public bool IsCompleted { get; init; }

    /// <summary>How this item changed. The value is Unchanged in a current plan view.</summary>
    public CoachPlanItemChangeKind ChangeKind { get; init; }

    /// <summary>The title of the resource for this item. Null if the item uses no resource.</summary>
    public string? ResourceTitle { get; init; }
}
