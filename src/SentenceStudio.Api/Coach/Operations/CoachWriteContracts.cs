using System.ComponentModel;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// The learner-visible content of a proposal or a receipt, as it is stored under protection.
/// </summary>
/// <remarks>
/// <see cref="SchemaVersion"/> travels inside the ciphertext. A payload written by an older build
/// that this build cannot understand is treated as unavailable — the caller is told the undo data
/// is from an older version — rather than being partially deserialized into a write.
/// </remarks>
/// <param name="SchemaVersion">The payload schema this row was written with.</param>
/// <param name="Summary">A one-line description of the change.</param>
/// <param name="Lines">Bounded detail lines shown under the summary.</param>
public sealed record CoachWriteNarrative(int SchemaVersion, string Summary, IReadOnlyList<string> Lines)
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// The prior values a reversible operation captured, as stored under protection.
/// </summary>
/// <param name="SchemaVersion">The payload schema this row was written with.</param>
/// <param name="StateJson">Handler-specific canonical JSON describing what to restore.</param>
public sealed record CoachWritePriorState(int SchemaVersion, string StateJson)
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// What a handler produced when asked to preview a write it has not performed yet.
/// </summary>
/// <param name="Summary">A one-line description of what would change.</param>
/// <param name="Lines">Bounded detail lines describing the change.</param>
/// <param name="EntityId">The entity the write targets, when it already exists.</param>
/// <param name="CanonicalArgumentsJson">
/// The handler's own normalized rendering of its arguments. This — not the model's raw call — is
/// what the idempotency digest and the confirmation binding are computed over, so two calls that
/// differ only in whitespace or property order are correctly recognised as the same request.
/// </param>
public sealed record CoachWritePreview(
    string Summary,
    IReadOnlyList<string> Lines,
    string? EntityId,
    string CanonicalArgumentsJson);

/// <summary>What a handler produced after performing a write.</summary>
/// <param name="Summary">A one-line description of what changed.</param>
/// <param name="Lines">Bounded detail lines describing the change.</param>
/// <param name="EntityId">The affected entity identifier.</param>
/// <param name="PriorStateJson">
/// Canonical JSON the same handler can consume to reverse the write, or null when the operation
/// is not reversible.
/// </param>
public sealed record CoachWriteExecution(
    string Summary,
    IReadOnlyList<string> Lines,
    string? EntityId,
    string? PriorStateJson);

/// <summary>
/// A proposal as it is handed back to the caller.
/// </summary>
/// <remarks>
/// This is the shape the model sees. It deliberately contains no confirmation secret, no owner
/// identifier, and no database keys other than the opaque operation id: the model's only power
/// over a write is to ask for one.
/// </remarks>
public sealed record CoachWriteProposalResult(
    [property: Description("Opaque identifier for this proposed change. Give it to the learner so they can accept or decline.")]
    string OperationId,
    [property: Description("The tool that produced the proposal.")]
    string ToolName,
    [property: Description("How the change must be approved: 'accept' for an ordinary change, 'confirm' for a protected one.")]
    string ApprovalMode,
    [property: Description("A one-line description of what would change. Nothing has changed yet.")]
    string Summary,
    [property: Description("Detail lines describing exactly what would change.")]
    IReadOnlyList<string> Lines,
    [property: Description("UTC instant after which this proposal can no longer be approved.")]
    DateTime ExpiresAtUtc,
    [property: Description("True when this repeats a proposal already recorded in this conversation, so no duplicate was created.")]
    bool IsDuplicate,
    [property: Description("True when the change has already been carried out and this is the stored receipt.")]
    bool AlreadyExecuted);

/// <summary>The durable receipt for an executed or reversed write.</summary>
public sealed record CoachWriteReceipt(
    string OperationId,
    string ToolName,
    CoachToolRiskClass RiskClass,
    CoachWriteOperationStatus Status,
    CoachWriteEntityKind EntityKind,
    string? EntityId,
    string Summary,
    IReadOnlyList<string> Lines,
    DateTime ExecutedAtUtc,
    bool CanUndo,
    DateTime? UndoExpiresAtUtc,
    string? UndoOperationId);

/// <summary>The approval channel an operation requires.</summary>
public static class CoachWriteApprovalModes
{
    /// <summary>A soft learner-owned write: explicit acceptance, then execution.</summary>
    public const string Accept = "accept";

    /// <summary>A protected write: a one-use confirmation secret, then execution.</summary>
    public const string Confirm = "confirm";
}
