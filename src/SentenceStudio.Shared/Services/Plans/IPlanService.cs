using SentenceStudio.Contracts.Plans;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Server-side orchestrator for the daily plan API surface. Wraps the
/// deterministic / LLM generators, persistence (DailyPlan + DailyPlanCompletion),
/// and the per-request user scope + date context.
///
/// All methods rely on the request's <c>IUserScopeProvider</c> + <c>IPlanDateContext</c>;
/// callers don't pass user id or "today" explicitly. Missing user claim throws
/// <see cref="UnauthorizedAccessException"/> at the scope layer — endpoints map
/// to 401.
/// </summary>
public interface IPlanService
{
    /// <summary>Return the user's plan for today (user-local), or null if none exists.
    /// Does NOT auto-generate.</summary>
    Task<TodaysPlanDto?> GetTodayAsync(CancellationToken ct = default);

    /// <summary>Generate (or regenerate) the user's plan for today. Upserts the
    /// <c>DailyPlan</c> row and replaces the <c>DailyPlanCompletion</c> child
    /// rows for the same date. Per-item progress already recorded for matching
    /// deterministic ids is preserved by merge.</summary>
    Task<TodaysPlanDto> GenerateTodayAsync(GenerateTodaysPlanRequest request, CancellationToken ct = default);

    /// <summary>Set-style update of <c>MinutesSpent</c> on a single plan item
    /// (clamped to 0..240). Idempotent.</summary>
    Task<bool> UpdateProgressAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default);

    /// <summary>Mark the item complete + capture <c>CompletedAtUtc</c>. Idempotent;
    /// repeats clamp <c>MinutesSpent</c> to the highest reported value.</summary>
    Task<PlanItemDto?> MarkCompleteAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default);

    /// <summary>Delete today's plan (and its child completion rows) so the user
    /// can regenerate from a clean slate. Idempotent.</summary>
    Task ResetTodayAsync(CancellationToken ct = default);

    /// <summary>
    /// Return the normalized snapshot (and therefore the stable plan version)
    /// of the user's plan for today. Returns an empty snapshot when no plan
    /// exists, so callers always get a comparable version token.
    /// </summary>
    Task<PlanSnapshot> GetTodaySnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Build a read-only plan preview for the scoped user under the supplied
    /// constraints. Performs zero database writes and distinguishes invalid
    /// constraints from an infeasible plan instead of returning null.
    /// </summary>
    Task<PlanPreviewResult> PreviewPlanAsync(PlanConstraints? constraints, CancellationToken ct = default);

    /// <summary>
    /// Preview overload carrying a vocabulary focus set already resolved against
    /// the trusted user scope. The planner treats the ids as authoritative, so
    /// the previewed plan and a subsequent apply carry identical words.
    /// </summary>
    Task<PlanPreviewResult> PreviewPlanAsync(
        PlanConstraints? constraints,
        IReadOnlyList<string>? focusVocabularyWordIds,
        CancellationToken ct = default);

    /// <summary>
    /// Apply validated coach constraints to today's plan inside one
    /// transaction. Rejects a stale expected version, preserves completed items
    /// unchanged, preserves every started item and its logged minutes, and
    /// replaces only untouched unfinished items. Never writes coach session or
    /// revision rows — it returns the normalized receipt for the API Coach
    /// persistence lane to record.
    /// </summary>
    Task<PlanRevisionResult> ApplyCoachConstraintsAsync(CoachPlanRevisionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Restore a previously captured remaining-plan snapshot through the same
    /// ownership, version, and validation path as
    /// <see cref="ApplyCoachConstraintsAsync"/>. Never alters completed work and
    /// never decreases logged minutes.
    /// </summary>
    Task<PlanRevisionResult> UndoCoachRevisionAsync(CoachPlanUndoRequest request, CancellationToken ct = default);
}
