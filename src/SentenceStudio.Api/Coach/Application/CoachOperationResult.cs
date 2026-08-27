using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The typed outcome of a coach application operation. Endpoints translate these into HTTP;
/// nothing below the endpoint layer knows about status codes.
/// </summary>
public enum CoachOperationStatus
{
    /// <summary>An unexpected server-side failure.</summary>
    Failed = 0,

    /// <summary>The operation succeeded.</summary>
    Ok,

    /// <summary>The coach is off, or the learner is outside the cohort, or there is no plan to edit.</summary>
    Unavailable,

    /// <summary>No owned session with that id. A session owned by someone else lands here too.</summary>
    SessionNotFound,

    /// <summary>The session exists but has expired or was written by an older agent configuration.</summary>
    SessionExpired,

    /// <summary>The submitted turn is malformed, too long, or of an unsupported shape.</summary>
    InvalidInput,

    /// <summary>The requested constraint change is outside the allowed ranges.</summary>
    InvalidConstraint,

    /// <summary>The constraints are valid but no plan satisfies them.</summary>
    NoFeasiblePlan,

    /// <summary>The named suggestion is not the session's current pending suggestion.</summary>
    SuggestionNotFound,

    /// <summary>Today's Plan changed since the client read it. Nothing was written.</summary>
    PlanChangedElsewhere,

    /// <summary>Another coach run for this learner is still in flight.</summary>
    RunInProgress,

    /// <summary>The learner is over the configured daily or weekly run budget.</summary>
    RateLimited,

    /// <summary>No chat client is configured on this host.</summary>
    ModelUnavailable,

    /// <summary>There is no applied, not-yet-undone revision to undo.</summary>
    NothingToUndo,

    /// <summary>
    /// The turn was cancelled before anything was applied. Distinct from a failure: nothing went
    /// wrong, and nothing changed.
    /// </summary>
    RunCancelled,

    /// <summary>An unrecoverable internal error after a retry was already attempted.</summary>
    InternalError
}

/// <summary>A typed application result plus the problem metadata an endpoint needs.</summary>
public sealed record CoachOperationResult<T>
{
    public required CoachOperationStatus Status { get; init; }

    public T? Value { get; init; }

    /// <summary>An operator- and learner-safe explanation. Never contains learner text.</summary>
    public string? Detail { get; init; }

    /// <summary>The RFC 7807 problem type from <see cref="CoachProblemTypes"/>.</summary>
    public string? ProblemType { get; init; }

    public bool IsOk => Status == CoachOperationStatus.Ok;

    /// <summary>
    /// True when the agent session was malformed and the turn needs a context rebuild from
    /// the conversation ledger before retrying. Only <see cref="CoachConversationService"/>
    /// can act on this signal.
    /// </summary>
    public bool RequiresRebuild { get; init; }

    public static CoachOperationResult<T> Ok(T value) =>
        new() { Status = CoachOperationStatus.Ok, Value = value };

    public static CoachOperationResult<T> Problem(CoachOperationStatus status, string problemType, string detail) =>
        new() { Status = status, ProblemType = problemType, Detail = detail };

    public static CoachOperationResult<T> NeedsRebuild() =>
        new() { Status = CoachOperationStatus.Ok, RequiresRebuild = true };
}
