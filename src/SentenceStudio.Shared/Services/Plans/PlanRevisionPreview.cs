namespace SentenceStudio.Services.Plans;

/// <summary>
/// The single definition of how a validated constraint change turns the current plan plus a
/// pure planner preview into the plan the learner would end up with.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPlanService.PreviewPlanAsync"/> answers a different question from
/// <see cref="IPlanService.ApplyCoachConstraintsAsync"/>. The preview is the deterministic
/// planner's <b>whole remainder</b> for the supplied constraints: every item starts at zero
/// minutes, nothing is completed, and priorities start from the top. The apply path never
/// replaces the plan with that. It keeps every completed and started row byte-identical,
/// slots the new remainder in <b>after</b> everything the learner has touched, and drops any
/// preview item whose id is already preserved.
/// </para>
/// <para>
/// Diffing the current plan straight against a raw preview therefore reports every completed
/// and started row as <c>Removed</c> and every preserved count as zero — a preview that
/// promises to throw away finished work the apply would never touch. <see cref="Merge"/>
/// closes that gap: it produces the exact snapshot the apply path would persist, so a
/// suggestion preview and the revision that follows it agree on the plan version, the hash,
/// the diff, and the estimated minutes.
/// </para>
/// <para>
/// This is a pure function. It reads two snapshots and returns a third; it opens no context,
/// touches no database, and cannot write.
/// </para>
/// </remarks>
public static class PlanRevisionPreview
{
    /// <summary>
    /// True when the learner has invested something in this item, which means a revision must
    /// leave it exactly as it is.
    /// </summary>
    public static bool IsPreserved(PlanSnapshotItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsCompleted || item.MinutesSpent > 0;
    }

    /// <summary>
    /// Merges a pure planner preview into the current plan using the apply path's
    /// preservation rules, and returns the resulting plan snapshot.
    /// </summary>
    /// <param name="current">The learner's current plan for the day.</param>
    /// <param name="previewRemainder">
    /// The planner's preview for the proposed constraints, as returned by
    /// <see cref="IPlanService.PreviewPlanAsync"/>. Its priorities are relative to the top of
    /// the plan and are re-based here, exactly as the apply path re-bases them.
    /// </param>
    public static PlanSnapshot Merge(PlanSnapshot current, PlanSnapshot previewRemainder)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previewRemainder);

        var preserved = current.Items.Where(IsPreserved).ToList();
        var preservedIds = new HashSet<string>(preserved.Select(i => i.PlanItemId), StringComparer.Ordinal);
        var priorityOffset = preserved.Count == 0 ? 0 : preserved.Max(i => i.Priority);

        // A preview item that is already preserved is dropped rather than duplicated: the
        // learner's own row wins, with its logged minutes intact.
        var replacement = previewRemainder.Items
            .Where(i => !preservedIds.Contains(i.PlanItemId))
            .Select(i => new PlanSnapshotItem
            {
                PlanItemId = i.PlanItemId,
                ActivityType = i.ActivityType,
                ResourceId = i.ResourceId,
                SkillId = i.SkillId,
                Priority = priorityOffset + i.Priority,
                EstimatedMinutes = i.EstimatedMinutes,
                MinutesSpent = 0,
                IsCompleted = false
            });

        return PlanSnapshot.FromItems(current.PlanDate, preserved.Concat(replacement));
    }

    /// <summary>
    /// The part of a merged plan a revision actually authored: the untouched remaining work.
    /// Preserved rows are excluded because no constraint can claim credit for them.
    /// </summary>
    public static IReadOnlyList<PlanSnapshotItem> Remainder(PlanSnapshot merged)
    {
        ArgumentNullException.ThrowIfNull(merged);
        return merged.Items.Where(i => !IsPreserved(i)).ToList();
    }
}
