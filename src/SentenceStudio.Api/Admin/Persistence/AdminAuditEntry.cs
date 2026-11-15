namespace SentenceStudio.Api.Admin.Persistence;

/// <summary>How an audited admin request ended.</summary>
public enum AdminAuditOutcome
{
    /// <summary>
    /// The request was admitted and its row written; the ending was never recorded. The request
    /// may or may not have done what it asked.
    /// </summary>
    Started = 0,

    Succeeded = 1,

    Failed = 2
}

/// <summary>
/// One admitted admin request: who, what, on which learner or report, when, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>No content.</b> No email, no name, no learner text, no request or response body. The actor
/// and the target are opaque ids, and everything else is a closed code. A copy of this table says
/// who looked at what and when; it does not say what they saw.
/// </para>
/// <para>
/// The row is inserted before the handler runs and updated when it finishes, so a read of private
/// data is on record before any of it is produced and a change that committed can never be missing.
/// </para>
/// </remarks>
public sealed class AdminAuditEntry
{
    public long Id { get; set; }

    /// <summary>When the request was admitted.</summary>
    public DateTime OccurredAtUtc { get; set; }

    public AdminActorKind ActorKind { get; set; }

    /// <summary>The Identity user id of the admin, or the agent token's id.</summary>
    public string ActorId { get; set; } = string.Empty;

    /// <summary>A closed code from <see cref="AdminActions"/>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>What kind of thing was acted on (<see cref="AdminTargetKinds"/>), when there is one.</summary>
    public string? TargetKind { get; set; }

    /// <summary>The id of the learner or report acted on, when there is one.</summary>
    public string? TargetId { get; set; }

    public AdminAuditOutcome Outcome { get; set; } = AdminAuditOutcome.Started;

    /// <summary>The HTTP status the request ended with.</summary>
    public int? StatusCode { get; set; }

    /// <summary>A closed code from <see cref="AdminFailureCodes"/>. Null unless it failed for a named reason.</summary>
    public string? FailureCode { get; set; }
}
