using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Why a pure plan preview did or did not produce a plan. Coach callers switch
/// on this instead of interpreting <c>null</c>.
/// </summary>
public enum PlanPreviewOutcome
{
    /// <summary>The preview produced a plan.</summary>
    Success = 0,

    /// <summary>One or more constraint fields were out of range. Nothing was built.</summary>
    InvalidConstraints,

    /// <summary>Constraints were valid but no activity survived them within the budget.</summary>
    NoFeasiblePlan,

    /// <summary>The scoped user has no profile, so no plan can be generated.</summary>
    UserProfileNotFound
}

/// <summary>
/// The result of a read-only plan preview. A preview performs zero database
/// writes; the returned snapshot is projected, not persisted.
/// </summary>
public sealed record PlanPreviewResult
{
    public required PlanPreviewOutcome Outcome { get; init; }

    /// <summary>The generated skeleton. Non-null only when <see cref="Outcome"/> is Success.</summary>
    public PlanSkeleton? Skeleton { get; init; }

    /// <summary>
    /// The normalized projection of the previewed plan. Non-null only on
    /// success. Its <c>Version</c> doubles as the stable preview identifier —
    /// identical constraints on identical inputs yield an identical value.
    /// </summary>
    public PlanSnapshot? Snapshot { get; init; }

    /// <summary>Constraint validation messages. Populated only for InvalidConstraints.</summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();

    public bool IsSuccess => Outcome == PlanPreviewOutcome.Success;

    /// <summary>The stable preview identifier, or null when there is no plan.</summary>
    public string? PreviewId => Snapshot?.Version;

    public static PlanPreviewResult Success(PlanSkeleton skeleton, PlanSnapshot snapshot) =>
        new() { Outcome = PlanPreviewOutcome.Success, Skeleton = skeleton, Snapshot = snapshot };

    public static PlanPreviewResult InvalidConstraints(IReadOnlyList<string> errors) =>
        new() { Outcome = PlanPreviewOutcome.InvalidConstraints, ValidationErrors = errors };

    public static PlanPreviewResult NoFeasiblePlan() =>
        new() { Outcome = PlanPreviewOutcome.NoFeasiblePlan };

    public static PlanPreviewResult UserProfileNotFound() =>
        new() { Outcome = PlanPreviewOutcome.UserProfileNotFound };
}

/// <summary>
/// Why a plan revision did or did not write. Every non-Applied outcome
/// guarantees zero writes.
/// </summary>
public enum PlanRevisionOutcome
{
    /// <summary>The revision was validated and written.</summary>
    Applied = 0,

    /// <summary>
    /// The revision was valid but would not have changed the stored plan, so
    /// nothing was written. Repeating an already-applied revision lands here,
    /// which is what makes apply safely repeatable.
    /// </summary>
    NoChange,

    /// <summary>The caller's expected plan version no longer matches. Nothing was written.</summary>
    StalePlanVersion,

    /// <summary>One or more constraint fields were out of range. Nothing was written.</summary>
    InvalidConstraints,

    /// <summary>No activity survived the constraints within the budget. Nothing was written.</summary>
    NoFeasiblePlan,

    /// <summary>There is no stored plan for the user-local date. Nothing was written.</summary>
    PlanNotFound,

    /// <summary>The revised plan failed a structural invariant, so the transaction rolled back.</summary>
    ValidationFailed
}

/// <summary>
/// The normalized receipt for one plan revision attempt.
/// </summary>
/// <remarks>
/// This is the hand-off to the API Coach persistence lane: it carries the
/// before/after versions, hashes, and normalized snapshots that
/// <c>CoachPlanRevisionInput</c> requires, plus the preservation counts the
/// change receipt reports. Shared never writes coach session or revision rows.
/// It holds no learner text.
/// </remarks>
public sealed record PlanRevisionResult
{
    public required PlanRevisionOutcome Outcome { get; init; }

    /// <summary>Echo of the caller's operation key, for the caller's own dedupe guard.</summary>
    public string? OperationKey { get; init; }

    /// <summary>The plan as it stood before the attempt. Null only when no plan exists.</summary>
    public PlanSnapshot? Before { get; init; }

    /// <summary>
    /// The plan after the attempt. Equal to <see cref="Before"/> for every
    /// non-Applied outcome, so callers can always diff safely.
    /// </summary>
    public PlanSnapshot? After { get; init; }

    public int PreservedCompletedCount { get; init; }
    public int PreservedInProgressCount { get; init; }

    /// <summary>Logged minutes carried across the revision. Never decreases.</summary>
    public int PreservedMinutesSpent { get; init; }

    public int ReplacedItemCount { get; init; }
    public int AddedItemCount { get; init; }
    public int RemovedItemCount { get; init; }
    public int AdjustedItemCount { get; init; }

    /// <summary>Validation or constraint messages. Empty when the attempt succeeded.</summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();

    public bool IsApplied => Outcome == PlanRevisionOutcome.Applied;

    /// <summary>True when the attempt completed without error, whether or not it wrote.</summary>
    public bool IsSuccessful => Outcome is PlanRevisionOutcome.Applied or PlanRevisionOutcome.NoChange;

    public string? BeforePlanVersion => Before?.Version;
    public string? AfterPlanVersion => After?.Version;
    public string? BeforePlanHash => Before?.Hash;
    public string? AfterPlanHash => After?.Hash;

    /// <summary>A no-write result that reports the current plan unchanged.</summary>
    public static PlanRevisionResult NoWrite(
        PlanRevisionOutcome outcome,
        PlanSnapshot? current,
        string? operationKey,
        IReadOnlyList<string>? errors = null) =>
        new()
        {
            Outcome = outcome,
            OperationKey = operationKey,
            Before = current,
            After = current,
            PreservedCompletedCount = current?.CompletedItemCount ?? 0,
            PreservedInProgressCount = current?.InProgressItemCount ?? 0,
            PreservedMinutesSpent = current?.TotalMinutesSpent ?? 0,
            ValidationErrors = errors ?? Array.Empty<string>()
        };
}
