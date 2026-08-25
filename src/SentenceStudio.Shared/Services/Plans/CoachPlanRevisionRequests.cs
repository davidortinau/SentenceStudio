namespace SentenceStudio.Services.Plans;

/// <summary>
/// A request to revise today's plan from validated coach constraints.
/// </summary>
/// <remarks>
/// User scope is deliberately absent: the service resolves it from
/// <c>IUserScopeProvider</c>, so no caller — and no model output — can address
/// another learner's plan.
/// </remarks>
public sealed record CoachPlanRevisionRequest
{
    /// <summary>
    /// The constraints to apply. <c>null</c> regenerates with default planner
    /// behavior, which is how "clear my constraints" is expressed.
    /// </summary>
    public PlanConstraints? Constraints { get; init; }

    /// <summary>
    /// The plan version the caller believes is current. When supplied and
    /// stale, the service writes nothing and reports
    /// <see cref="PlanRevisionOutcome.StalePlanVersion"/>. Null skips the check.
    /// </summary>
    public string? ExpectedPlanVersion { get; init; }

    /// <summary>
    /// Caller-supplied idempotency/correlation key. Echoed on the result. The
    /// service does not persist it; the API Coach persistence lane guards
    /// duplicates with it alongside the returned before/after versions.
    /// </summary>
    public string? OperationKey { get; init; }

    /// <summary>
    /// A vocabulary focus set already resolved against the trusted user scope by
    /// <c>IVocabularyFocusResolver</c>. The apply reuses exactly these ids, so
    /// the plan it writes matches the previewed plan word for word.
    /// </summary>
    public IReadOnlyList<string>? FocusVocabularyWordIds { get; init; }

    /// <summary>Coach session correlation, for logging only. Never persisted here.</summary>
    public string? SessionId { get; init; }

    /// <summary>Client turn correlation, for logging only. Never persisted here.</summary>
    public string? ClientTurnId { get; init; }
}

/// <summary>
/// A request to restore a previously captured remaining-plan snapshot.
/// </summary>
/// <remarks>
/// Undo replays a normalized snapshot rather than re-running the planner, so it
/// is exact and cannot drift with the clock or the SRS queue. It travels the
/// same ownership, version, and validation path as an apply: completed work is
/// never altered and logged minutes never decrease.
/// </remarks>
public sealed record CoachPlanUndoRequest
{
    /// <summary>The snapshot to restore. Normally the revision's before-snapshot.</summary>
    public required PlanSnapshot TargetSnapshot { get; init; }

    /// <summary>The plan version the caller believes is current. Null skips the check.</summary>
    public string? ExpectedPlanVersion { get; init; }

    /// <summary>Caller-supplied idempotency/correlation key. Echoed on the result.</summary>
    public string? OperationKey { get; init; }

    /// <summary>Coach session correlation, for logging only. Never persisted here.</summary>
    public string? SessionId { get; init; }

    /// <summary>The revision being undone, for logging only. Never persisted here.</summary>
    public string? RevisionId { get; init; }
}
