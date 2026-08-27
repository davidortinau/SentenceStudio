using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// One immutable entry in the canonical visible-message ledger.
/// </summary>
/// <remarks>
/// <para>
/// This is the source of truth for what the learner saw. It is append-only: there is no update
/// path in any store, and the only deletion is the conversation purge cascade.
/// </para>
/// <para>
/// <see cref="ProtectedPayload"/> holds the encrypted <see cref="CoachMessagePayload"/>. Raw
/// prompts, developer or system instructions, chain-of-thought, tool arguments, tool results,
/// internal vocabulary identifiers, agent-session state, and provider traces are never ledger
/// content — a message row carries only what the learner is entitled to read back.
/// </para>
/// </remarks>
public sealed class CoachMessage
{
    /// <summary>Opaque application-owned identifier, stable for UI identity.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The owning learner. The only ownership authority.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Forward-compatibility classification. Never queried and never keyed.</summary>
    public string? TenantId { get; set; }

    /// <summary>The conversation this message belongs to.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Strictly increasing, gap-free order within the conversation. Immutable once written and
    /// enforced by a unique index, so a retry can never duplicate or skip a position.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>Who produced the message.</summary>
    public CoachMessageRole Role { get; set; }

    /// <summary>How the message renders.</summary>
    public CoachMessageKind Kind { get; set; }

    /// <summary>The encrypted typed payload. Ciphertext, never JSON, never typed as jsonb.</summary>
    public string ProtectedPayload { get; set; } = string.Empty;

    /// <summary>The payload contract version, so an older row can still be projected.</summary>
    public int ContentSchemaVersion { get; set; }

    /// <summary>The protector envelope version used for <see cref="ProtectedPayload"/>.</summary>
    public int ContentProtectionVersion { get; set; }

    /// <summary>The turn operation that produced the message, for durable correlation.</summary>
    public string? OperationId { get; set; }

    /// <summary>The canonical server timestamp (UTC) shown to the learner.</summary>
    public DateTime CreatedAt { get; set; }
}
