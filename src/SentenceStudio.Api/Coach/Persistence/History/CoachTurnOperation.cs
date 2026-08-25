namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// The durable record of one turn: idempotency, single-writer lease, cancellation, and outcome.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the process-local retry and cancellation state with something that survives a
/// restart and works across replicas. A retry finds the same row; a crashed worker's lease
/// expires and another worker claims it with a higher <see cref="FencingVersion"/>, which makes
/// the old worker's finalization fail instead of appending duplicate output.
/// </para>
/// <para>
/// <see cref="ProtectedRequestDigest"/> is the encrypted digest of the canonical request bytes.
/// It is protected rather than stored bare because a bare hash of short learner text is
/// brute-forceable: an attacker with database access could confirm a guessed sentence. Encrypting
/// it keeps same-key/different-payload detection exact while disclosing neither the plaintext nor
/// a low-entropy hash.
/// </para>
/// </remarks>
public sealed class CoachTurnOperation
{
    /// <summary>Opaque application-owned operation identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The owning learner. The only ownership authority.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Forward-compatibility classification. Never queried and never keyed.</summary>
    public string? TenantId { get; set; }

    /// <summary>The conversation this operation writes to.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Owner-bound digest of the client's idempotency key. The key itself is never stored, and
    /// the digest is salted with the owner and conversation so the same key used in two
    /// conversations produces two unrelated values.
    /// </summary>
    public string IdempotencyKeyDigest { get; set; } = string.Empty;

    /// <summary>The encrypted digest of the canonical request bytes.</summary>
    public string ProtectedRequestDigest { get; set; } = string.Empty;

    /// <summary>The protector envelope version used for the request digest and outcome.</summary>
    public int ContentProtectionVersion { get; set; }

    /// <summary>The conversation version the operation was accepted against.</summary>
    public int BaseConversationVersion { get; set; }

    /// <summary>The durable operation state.</summary>
    public CoachTurnOperationStatus Status { get; set; } = CoachTurnOperationStatus.Pending;

    /// <summary>The worker identity that currently holds the lease, if any.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the current lease stops being valid (UTC). Null when no lease is held.</summary>
    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>
    /// Monotonic fencing counter. Every successful claim increments it, so a worker whose lease
    /// expired cannot finalize after a newer worker took over.
    /// </summary>
    public long FencingVersion { get; set; }

    /// <summary>How many workers have claimed the operation.</summary>
    public int AttemptCount { get; set; }

    /// <summary>True once cancellation was durably requested. Never reset to false.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>The already-committed learner message sequence this turn responds to.</summary>
    public long? LearnerMessageSequence { get; set; }

    /// <summary>The first response sequence this turn appended.</summary>
    public long? FirstResponseSequence { get; set; }

    /// <summary>The last response sequence this turn appended.</summary>
    public long? LastResponseSequence { get; set; }

    /// <summary>The encrypted durable outcome replayed verbatim for a completed operation.</summary>
    public string? ProtectedOutcome { get; set; }

    /// <summary>The outcome payload contract version.</summary>
    public int? OutcomeSchemaVersion { get; set; }

    /// <summary>A closed, content-free operational failure code. Never a message or learner text.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Row concurrency token, incremented by every write.</summary>
    public int Version { get; set; }

    /// <summary>When the operation was first accepted (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the row last changed (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>When a worker first claimed the operation (UTC).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the operation reached a terminal state (UTC).</summary>
    public DateTime? CompletedAt { get; set; }
}
