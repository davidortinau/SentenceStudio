using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// One server-owned Learning Coach session. This row is the only place the resumable
/// agent conversation state lives, and it is stored encrypted. Deleting the row is the
/// hard delete of that conversation state and of any pending suggestion.
/// </summary>
/// <remarks>
/// This entity carries no raw learner text as a first-class column. The learner's words
/// exist only inside <see cref="ProtectedAgentSession"/>, which is protected at rest by
/// <see cref="ICoachAgentSessionProtector"/> and bound to this row: the ciphertext is
/// encrypted under a purpose chain that includes <see cref="UserProfileId"/> and
/// <see cref="Id"/>, so a payload copied into another learner's row does not decrypt.
/// </remarks>
public sealed class CoachSession
{
    /// <summary>Application-owned identifier. EF never generates this value.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Owning learner. Required and indexed. Every store query filters on it.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>The coach implementation that created the session ("baseline" or "harness").</summary>
    public string AgentImplementation { get; set; } = string.Empty;

    /// <summary>The agent name used for this session.</summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>
    /// The coach configuration version (instructions, tools, policy) in force when the
    /// session was created, copied from <c>Coach:AgentConfigVersion</c>. A load under a
    /// different current version is rejected.
    /// </summary>
    public string AgentConfigVersion { get; set; } = string.Empty;

    /// <summary>
    /// The serialized-session schema version. A load with a different current version is
    /// rejected because the stored agent state can no longer be rehydrated safely.
    /// </summary>
    public int SessionSchemaVersion { get; set; }

    /// <summary>
    /// The encrypted serialized agent session. Null until the first turn completes.
    /// The raw database value is ciphertext and never contains the plaintext JSON.
    /// </summary>
    public string? ProtectedAgentSession { get; set; }

    /// <summary>Normalized active constraint set, serialized as JSON.</summary>
    public string ActiveConstraintsJson { get; set; } = string.Empty;

    /// <summary>The identifier of the suggestion awaiting a decision. Null when none is pending.</summary>
    public string? PendingSuggestionId { get; set; }

    /// <summary>Normalized pending constraint delta, serialized as JSON. Null when none is pending.</summary>
    public string? PendingSuggestionDeltaJson { get; set; }

    /// <summary>When the pending suggestion was created. Null when none is pending.</summary>
    public DateTime? PendingSuggestionCreatedAt { get; set; }

    /// <summary>Number of learner turns processed in this session.</summary>
    public int TurnCount { get; set; }

    /// <summary>Number of clarification questions the coach asked in this session.</summary>
    public int ClarificationCount { get; set; }

    /// <summary>Number of applied plan revisions produced by this session.</summary>
    public int RevisionCount { get; set; }

    /// <summary>The session status.</summary>
    public CoachSessionStatus Status { get; set; } = CoachSessionStatus.Active;

    /// <summary>Why the session stopped. Null while the session is still usable.</summary>
    public CoachStopReason? StopReason { get; set; }

    /// <summary>When the session was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the session was last written (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Sliding expiry. Reads and writes push this forward by the configured session
    /// lifetime (24 hours by default). A read past this instant is rejected.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}
