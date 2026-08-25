using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The audit payload the coach stores for one side of a plan revision.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="State"/> is exactly the client-facing plan state. <see cref="Restore"/> is the
/// planner's normalized <see cref="PlanSnapshot"/>, which additionally carries the resource
/// and skill ids an Undo needs to re-create an item the revision removed — those are not on
/// <c>CoachPlanItemDto</c> because clients never need them.
/// </para>
/// <para>
/// Neither half holds learner text, a vocabulary term, a gloss, or an example. It is plan
/// shape only.
/// </para>
/// </remarks>
public sealed record CoachRevisionSnapshotEnvelope
{
    /// <summary>
    /// The current envelope schema. Rows written before this member existed deserialize to
    /// <see cref="LegacyVersion"/>, which is the only signal that separates them from a current
    /// row whose constraints simply did not change.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>A row written before the schema carried a version.</summary>
    public const int LegacyVersion = 0;

    /// <summary>
    /// Defaults to <see cref="LegacyVersion"/>, not to the current one. A property initializer runs
    /// when the member is absent from the JSON, so defaulting to current would make every
    /// pre-versioning row claim to be current — which is exactly the confusion the member exists to
    /// remove. Writers set it explicitly.
    /// </summary>
    public int Version { get; init; } = LegacyVersion;

    public required CoachPlanStateDto State { get; init; }

    public required PlanSnapshot Restore { get; init; }

    /// <summary>
    /// The frozen vocabulary focus in force on this side of the revision, identifiers and counts
    /// only. The display words are stripped: this row is permanent, and the audit holds no
    /// vocabulary term or gloss.
    /// </summary>
    public CoachFocusSelection? FocusSelection { get; init; }
}

/// <summary>Maps planner snapshots onto the coach's client-facing plan contracts.</summary>
public sealed class CoachPlanProjection
{
    private readonly IPlanCopyProvider _copy;

    public CoachPlanProjection(IPlanCopyProvider copy)
    {
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
    }

    /// <summary>Projects a live plan snapshot onto the plan-canvas contract.</summary>
    public CoachPlanStateDto ToPlanState(
        PlanSnapshot snapshot,
        CoachConstraintSetDto appliedConstraints,
        CoachRevisionDto? lastRevision = null,
        bool canUndo = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(appliedConstraints);

        var items = snapshot.Items.Select(i => ToItem(i, ChangeKindFor(i))).ToList();
        var total = items.Count;
        var completed = items.Count(i => i.IsCompleted);

        return new CoachPlanStateDto
        {
            PlanDate = snapshot.PlanDate,
            PlanVersion = snapshot.Version,
            Items = items,
            AppliedConstraints = appliedConstraints,
            EstimatedTotalMinutes = snapshot.TotalEstimatedMinutes,
            CompletedCount = completed,
            TotalCount = total,
            CompletionPercentage = total == 0 ? 0 : Math.Round(completed * 100d / total, 2),
            LastRevision = lastRevision,
            CanUndo = canUndo
        };
    }

    /// <summary>
    /// Diffs two snapshots. <paramref name="isPreview"/> marks a diff that describes a
    /// pending suggestion — nothing has been written for it.
    /// </summary>
    public CoachPlanDiffDto ToDiff(PlanSnapshot before, PlanSnapshot after, bool isPreview)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeById = before.Items.ToDictionary(i => i.PlanItemId, StringComparer.Ordinal);
        var afterById = after.Items.ToDictionary(i => i.PlanItemId, StringComparer.Ordinal);

        var items = new List<CoachPlanItemDto>();
        var added = 0;
        var removed = 0;
        var adjusted = 0;
        var preservedCompleted = 0;
        var preservedInProgress = 0;

        foreach (var item in after.Items)
        {
            CoachPlanItemChangeKind kind;
            if (item.IsCompleted)
            {
                kind = CoachPlanItemChangeKind.PreservedCompleted;
                preservedCompleted++;
            }
            else if (item.MinutesSpent > 0)
            {
                kind = CoachPlanItemChangeKind.PreservedInProgress;
                preservedInProgress++;
            }
            else if (!beforeById.TryGetValue(item.PlanItemId, out var previous))
            {
                kind = CoachPlanItemChangeKind.Added;
                added++;
            }
            else if (previous.EstimatedMinutes != item.EstimatedMinutes || previous.Priority != item.Priority)
            {
                kind = CoachPlanItemChangeKind.Adjusted;
                adjusted++;
            }
            else
            {
                kind = CoachPlanItemChangeKind.Unchanged;
            }

            items.Add(ToItem(item, kind));
        }

        foreach (var item in before.Items.Where(i => !afterById.ContainsKey(i.PlanItemId)))
        {
            removed++;
            items.Add(ToItem(item, CoachPlanItemChangeKind.Removed));
        }

        return new CoachPlanDiffDto
        {
            BeforePlanVersion = before.Version,
            AfterPlanVersion = after.Version,
            IsPreview = isPreview,
            Items = items,
            AddedItemCount = added,
            RemovedItemCount = removed,
            AdjustedItemCount = adjusted,
            PreservedCompletedItemCount = preservedCompleted,
            PreservedInProgressItemCount = preservedInProgress,
            EstimatedMinutesBefore = before.TotalEstimatedMinutes,
            EstimatedMinutesAfter = after.TotalEstimatedMinutes
        };
    }

    private CoachPlanItemDto ToItem(PlanSnapshotItem item, CoachPlanItemChangeKind changeKind)
    {
        var activityType = ParseActivityType(item.ActivityType);

        // Titles come from the shared plan copy provider so the coach canvas reads exactly
        // like Today's Plan. Resource titles are resolved by the client from its own plan
        // data — the coach never joins resources here, which keeps this projection free of
        // any read that could surface embargoed content.
        var (title, description) = _copy.GetItemCopy(activityType, null, null, null);

        return new CoachPlanItemDto
        {
            Id = item.PlanItemId,
            ActivityType = ToCoachActivityType(activityType),
            Title = title,
            Description = description,
            Priority = item.Priority,
            EstimatedMinutes = item.EstimatedMinutes,
            MinutesSpent = item.MinutesSpent,
            IsCompleted = item.IsCompleted,
            ChangeKind = changeKind,
            ResourceTitle = null
        };
    }

    private static CoachPlanItemChangeKind ChangeKindFor(PlanSnapshotItem item) => item switch
    {
        { IsCompleted: true } => CoachPlanItemChangeKind.PreservedCompleted,
        { MinutesSpent: > 0 } => CoachPlanItemChangeKind.PreservedInProgress,
        _ => CoachPlanItemChangeKind.Unchanged
    };

    private static PlanActivityType ParseActivityType(string value) =>
        Enum.TryParse<PlanActivityType>(value, ignoreCase: false, out var parsed)
            ? parsed
            : PlanActivityType.VocabularyReview;

    private static CoachPlanActivityType ToCoachActivityType(PlanActivityType value) =>
        Enum.TryParse<CoachPlanActivityType>(value.ToString(), ignoreCase: false, out var parsed)
            ? parsed
            : CoachPlanActivityType.VocabularyReview;
}
