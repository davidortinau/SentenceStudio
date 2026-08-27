using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// One proposed learner-owned write, from the moment the model asked for it through execution
/// and any subsequent undo.
/// </summary>
/// <remarks>
/// <para>
/// This row is the operational state machine, not the audit. It carries the encrypted material
/// needed to execute the write later and to reverse it afterwards. The audit — safe metadata
/// only, no ciphertext at all — is <see cref="CoachWriteAudit"/>, so a reviewer can read the
/// audit table end to end and be certain it holds no learner content in any form.
/// </para>
/// <para>
/// Nothing here is written by the model. The model produces a proposal; the server assigns every
/// identifier, digest, and timestamp, and the confirmation secret never leaves the authenticated
/// owner-scoped route.
/// </para>
/// </remarks>
public sealed class CoachWriteOperation
{
    /// <summary>Server-assigned opaque operation identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The owning learner. Every read and every transition is filtered by this first.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Reserved for a future tenant boundary. Not part of any protection purpose chain.</summary>
    public string? TenantId { get; set; }

    /// <summary>The conversation the proposal belongs to.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The turn that produced the proposal, for correlating an audit trail to a transcript.</summary>
    public string? TurnId { get; set; }

    /// <summary>The registered tool name that produced this proposal.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>The registered risk class, copied at proposal time so the audit is self-contained.</summary>
    public CoachToolRiskClass RiskClass { get; set; }

    /// <summary>Where the operation is in its lifecycle.</summary>
    public CoachWriteOperationStatus Status { get; set; } = CoachWriteOperationStatus.Proposed;

    /// <summary>Whether — and how — this operation can be reversed once executed.</summary>
    public CoachWriteUndoKind UndoKind { get; set; } = CoachWriteUndoKind.None;

    /// <summary>The kind of entity the operation touches.</summary>
    public CoachWriteEntityKind EntityKind { get; set; } = CoachWriteEntityKind.None;

    /// <summary>
    /// The affected entity identifier once one exists. Opaque, carries no learner text, and is
    /// the only learner-data reference allowed to leave this table in plaintext.
    /// </summary>
    public string? EntityId { get; set; }

    /// <summary>
    /// Owner- and conversation-bound digest of (tool name + canonical arguments). Two identical
    /// proposals in the same conversation collide here and reuse one row; the same arguments in
    /// another conversation, or for another learner, produce an unrelated value.
    /// </summary>
    public string IdempotencyKeyDigest { get; set; } = string.Empty;

    /// <summary>
    /// Owner-, conversation-, operation- and argument-bound digest of the one-use confirmation
    /// secret. Null for soft writes, which need an explicit acceptance but not a secret. The
    /// secret itself is never stored, so a database copy cannot be used to confirm anything.
    /// </summary>
    public string? ConfirmationDigest { get; set; }

    /// <summary>
    /// When the outstanding confirmation secret stops being redeemable. Null until the learner
    /// opens the confirmation prompt, which is when the secret is minted.
    /// </summary>
    public DateTime? ConfirmationExpiresAtUtc { get; set; }

    /// <summary>Protected canonical arguments. Replayed verbatim at execution time.</summary>
    public string ProtectedArguments { get; set; } = string.Empty;

    /// <summary>
    /// Protected snapshot of the fields the operation is about to change, captured during
    /// execution and used only by undo. Null when the operation is not reversible.
    /// </summary>
    public string? ProtectedPriorState { get; set; }

    /// <summary>Protected learner-visible preview lines shown before acceptance.</summary>
    public string ProtectedPreview { get; set; } = string.Empty;

    /// <summary>Protected receipt, written once on execution and replayed on every repeat request.</summary>
    public string? ProtectedReceipt { get; set; }

    /// <summary>The content protection version the payload columns were written with.</summary>
    public int ContentProtectionVersion { get; set; }

    /// <summary>When the proposal stops being answerable.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When the undo window closes. Null unless the operation executed and is reversible.</summary>
    public DateTime? UndoExpiresAtUtc { get; set; }

    /// <summary>When the learner accepted or confirmed. Set exactly once, under the concurrency token.</summary>
    public DateTime? ExecutedAtUtc { get; set; }

    /// <summary>When the operation was reversed. Set exactly once, under the concurrency token.</summary>
    public DateTime? UndoneAtUtc { get; set; }

    /// <summary>The operation identifier of the undo's own ledger row, so the reversal is auditable too.</summary>
    public string? UndoOperationId { get; set; }

    /// <summary>When the row was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the row last changed.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Two accepts, or an accept racing an undo, resolve to one
    /// winner and one conflict rather than to two writes.
    /// </summary>
    public int Version { get; set; }
}
