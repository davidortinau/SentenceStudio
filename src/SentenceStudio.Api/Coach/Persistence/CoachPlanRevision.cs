using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// The normalized audit record for one applied coach revision of Today's Plan.
/// </summary>
/// <remarks>
/// This record deliberately holds NO raw learner text: no transcript, no prompt, no
/// clarification question, no coach message. It stores only the normalized constraint
/// delta, normalized plan snapshots, versions, hashes, and preservation counts.
/// Deleting a coach session does not delete these rows — applied plan changes remain
/// auditable for the configured retention window, and deleting coach history never
/// undoes Today's Plan.
/// </remarks>
public sealed class CoachPlanRevision
{
    /// <summary>Application-owned identifier. EF never generates this value.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Owning learner. Required and indexed. Every store query filters on it.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>The session that produced the revision. Required and indexed.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Monotonic revision number inside the session. The first revision is 1.</summary>
    public int RevisionNumber { get; set; }

    /// <summary>What caused the revision (direct request, accepted suggestion, undo).</summary>
    public CoachRevisionSource Source { get; set; }

    /// <summary>The validated intent type that produced the revision.</summary>
    public CoachIntentKind IntentKind { get; set; }

    /// <summary>Normalized accepted constraint delta, serialized as JSON.</summary>
    public string AcceptedConstraintDeltaJson { get; set; } = string.Empty;

    /// <summary>The plan version before the revision.</summary>
    public string BeforePlanVersion { get; set; } = string.Empty;

    /// <summary>The plan version after the revision.</summary>
    public string AfterPlanVersion { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the normalized before snapshot.</summary>
    public string BeforePlanHash { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the normalized after snapshot.</summary>
    public string AfterPlanHash { get; set; } = string.Empty;

    /// <summary>Normalized plan snapshot before the revision, serialized as JSON.</summary>
    public string BeforePlanSnapshotJson { get; set; } = string.Empty;

    /// <summary>Normalized plan snapshot after the revision, serialized as JSON.</summary>
    public string AfterPlanSnapshotJson { get; set; } = string.Empty;

    /// <summary>Completed items the revision preserved unchanged.</summary>
    public int PreservedCompletedCount { get; set; }

    /// <summary>Started-but-unfinished items the revision preserved with their progress.</summary>
    public int PreservedInProgressCount { get; set; }

    /// <summary>
    /// The durable turn operation that produced this revision, when one did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the correlation key that lets a receipt be rebuilt after a crash. Without it,
    /// recovery had to guess which revision belonged to the interrupted turn by looking for rows
    /// created after the operation started — a time window, which is wrong in both directions. Two
    /// conversations revising the same plan within the window would each find the other's
    /// revision, and a slow clock or a retry outside the window would find none and report "no
    /// change" for a change that had already been committed.
    /// </para>
    /// <para>
    /// Nullable because the column is additive over existing rows, and because not every revision
    /// comes from a durable turn: undo and legacy session paths write revisions too. A null means
    /// "not attributable to an operation", never "operation unknown".
    /// </para>
    /// </remarks>
    public string? OperationId { get; set; }

    /// <summary>When the server applied the revision (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>True once a later undo reversed this revision.</summary>
    public bool IsUndone { get; set; }

    /// <summary>When the undo happened (UTC). Null while the revision stands.</summary>
    public DateTime? UndoneAt { get; set; }

    /// <summary>The revision that performed the undo. Null while the revision stands.</summary>
    public string? UndoneByRevisionId { get; set; }
}
