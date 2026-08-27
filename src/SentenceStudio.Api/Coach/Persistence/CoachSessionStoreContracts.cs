using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>Why a session load did or did not produce a usable session.</summary>
public enum CoachSessionLoadStatus
{
    /// <summary>No session with that id is owned by the calling learner. Callers return 404.</summary>
    NotFound = 0,

    /// <summary>The session is owned, unexpired, and readable.</summary>
    Found,

    /// <summary>The session is owned but past its sliding expiry. Callers start a new session.</summary>
    Expired,

    /// <summary>
    /// The session was created under a different coach config or session-schema version.
    /// Its agent state cannot be rehydrated safely, so it is rejected.
    /// </summary>
    ConfigVersionMismatch,

    /// <summary>
    /// The stored agent state exists but could not be decrypted (key rotation or tampering).
    /// </summary>
    Unreadable
}

/// <summary>The outcome of a session load, including the decrypted agent-session JSON.</summary>
/// <param name="Status">Why the load succeeded or failed.</param>
/// <param name="Session">The owned session row. Null unless <paramref name="Status"/> is Found.</param>
/// <param name="AgentSessionJson">Decrypted agent-session JSON, or null when the session has no stored agent state yet.</param>
public sealed record CoachSessionLoadResult(
    CoachSessionLoadStatus Status,
    CoachSession? Session,
    string? AgentSessionJson)
{
    /// <summary>A load that found nothing the caller owns.</summary>
    public static CoachSessionLoadResult NotFound { get; } = new(CoachSessionLoadStatus.NotFound, null, null);

    /// <summary>True when the caller may use the session.</summary>
    public bool IsUsable => Status == CoachSessionLoadStatus.Found && Session is not null;
}

/// <summary>Everything needed to create one session row.</summary>
public sealed class CreateCoachSessionRequest
{
    /// <summary>Optional caller-supplied id. A new GUID string is used when omitted.</summary>
    public string? SessionId { get; init; }

    /// <summary>The coach implementation ("baseline" or "harness").</summary>
    public required string AgentImplementation { get; init; }

    /// <summary>The agent name used for this session.</summary>
    public required string AgentName { get; init; }

    /// <summary>The normalized constraint set the session starts from.</summary>
    public required CoachConstraintSetDto ActiveConstraints { get; init; }

    /// <summary>Optional serialized agent session to store encrypted at creation time.</summary>
    public string? AgentSessionJson { get; init; }
}

/// <summary>A partial update to one session row. A null member means "leave unchanged".</summary>
public sealed class CoachSessionUpdate
{
    /// <summary>Replacement serialized agent session. Stored encrypted.</summary>
    public string? AgentSessionJson { get; init; }

    /// <summary>Replacement normalized constraint set.</summary>
    public CoachConstraintSetDto? ActiveConstraints { get; init; }

    /// <summary>
    /// A pre-serialized active-state envelope, written verbatim to the active-constraints column.
    /// Takes precedence over <see cref="ActiveConstraints"/>. The application owns this shape so
    /// that server-only state — the frozen vocabulary focus selection — can ride in the existing
    /// JSON column without persistence needing to know what is in it.
    /// </summary>
    public string? ActiveStateJson { get; init; }

    /// <summary>Replacement session status.</summary>
    public CoachSessionStatus? Status { get; init; }

    /// <summary>Replacement stop reason. Set <see cref="ClearStopReason"/> to remove it.</summary>
    public CoachStopReason? StopReason { get; init; }

    /// <summary>True to clear the stop reason.</summary>
    public bool ClearStopReason { get; init; }

    /// <summary>Turns to add to the turn counter.</summary>
    public int TurnIncrement { get; init; }

    /// <summary>Clarifications to add to the clarification counter.</summary>
    public int ClarificationIncrement { get; init; }
}

/// <summary>The normalized inputs for one revision audit row. No learner text is accepted.</summary>
public sealed class CoachPlanRevisionInput
{
    /// <summary>Optional caller-supplied id. A new GUID string is used when omitted.</summary>
    public string? RevisionId { get; init; }

    /// <summary>
    /// The durable turn operation this revision belongs to, when one does.
    /// </summary>
    /// <remarks>
    /// Supplying it is what makes the revision findable again after a crash, without guessing
    /// from timestamps. Undo and legacy session paths leave it null.
    /// </remarks>
    public string? OperationId { get; init; }

    /// <summary>What caused the revision.</summary>
    public required CoachRevisionSource Source { get; init; }

    /// <summary>The validated intent type behind the revision.</summary>
    public required CoachIntentKind IntentKind { get; init; }

    /// <summary>The accepted constraint delta, already validated and normalized.</summary>
    public required CoachConstraintDeltaDto AcceptedDelta { get; init; }

    /// <summary>The plan version before the revision.</summary>
    public required string BeforePlanVersion { get; init; }

    /// <summary>The plan version after the revision.</summary>
    public required string AfterPlanVersion { get; init; }

    /// <summary>The normalized plan snapshot before the revision.</summary>
    public required CoachPlanStateDto BeforePlan { get; init; }

    /// <summary>The normalized plan snapshot after the revision.</summary>
    public required CoachPlanStateDto AfterPlan { get; init; }

    /// <summary>
    /// Optional verbatim JSON to store instead of serializing <see cref="BeforePlan"/>.
    /// </summary>
    /// <remarks>
    /// The application uses this to store a lossless restore envelope (plan state plus the
    /// planner's normalized snapshot) so an Undo can re-create an item the revision removed.
    /// It must stay plan-shape only — the audit never accepts learner text, and the entity
    /// still exposes no free-text column.
    /// </remarks>
    public string? BeforePlanAuditJson { get; init; }

    /// <inheritdoc cref="BeforePlanAuditJson"/>
    public string? AfterPlanAuditJson { get; init; }

    /// <summary>Completed items preserved unchanged.</summary>
    public required int PreservedCompletedCount { get; init; }

    /// <summary>Started items preserved with their logged progress.</summary>
    public required int PreservedInProgressCount { get; init; }
}
