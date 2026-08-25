namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// The bounds the write-operation ledger enforces before anything reaches the database.
/// </summary>
/// <remarks>
/// Encryption hides content but not size, so every plaintext bound is checked before protection.
/// The time windows are deliberately short: a proposal the learner has forgotten about must not
/// still be executable an hour later, and an undo offered indefinitely is a second delete button
/// wearing a friendly label.
/// </remarks>
public static class CoachWriteLimits
{
    /// <summary>Maximum length of an opaque identifier column.</summary>
    public const int IdMaxLength = 64;

    /// <summary>Maximum length of the owning user profile id.</summary>
    public const int UserProfileIdMaxLength = 64;

    /// <summary>Maximum length of the forward-compatibility tenant id.</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>Maximum length of a tool name column.</summary>
    public const int ToolNameMaxLength = 64;

    /// <summary>Maximum length of a stored digest column (hex SHA-256 is 64 characters).</summary>
    public const int DigestMaxLength = 128;

    /// <summary>Maximum length of a content-free operational failure code.</summary>
    public const int FailureCodeMaxLength = 64;

    /// <summary>Maximum length of one learner-visible preview or receipt line.</summary>
    public const int LineMaxLength = 400;

    /// <summary>Maximum number of learner-visible lines in a preview or receipt.</summary>
    public const int LineMax = 12;

    /// <summary>Maximum serialized argument payload size in bytes, measured before protection.</summary>
    public const int ArgumentsMaxBytes = 16 * 1024;

    /// <summary>Maximum serialized prior-state payload size in bytes, measured before protection.</summary>
    public const int PriorStateMaxBytes = 32 * 1024;

    /// <summary>Maximum serialized receipt payload size in bytes, measured before protection.</summary>
    public const int ReceiptMaxBytes = 16 * 1024;

    /// <summary>
    /// How long a proposal stays answerable. After this the row is refused and the learner has to
    /// ask again, which is cheap and keeps a stale proposal from executing against data that moved.
    /// </summary>
    public static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a protected confirmation stays redeemable. Shorter than a proposal, because the
    /// learner is looking at the confirmation prompt when the clock starts.
    /// </summary>
    public static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a reversible operation offers undo. One-use, and only inside this window.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an operation row is retained before the expiry sweep may remove it. Long enough
    /// for an operator to answer "what did Sam change for this learner", short enough that the
    /// ledger is not a second copy of the learner's data.
    /// </summary>
    public static readonly TimeSpan OperationRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// How many write proposals one turn may record. Exactly one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a tuning knob; it is the capacity of the surface, stated as a number. A turn
    /// carries one proposal to the client — the live turn response has a single
    /// <c>WriteOperation</c>, and rebuilt history anchors a single card to the turn's last coach
    /// message. A ledger that accepted a second row for the same turn would be accepting a row
    /// that no screen can ever show: not hidden by a bug, but unreachable by construction, while
    /// still being an approvable claim on the learner's data.
    /// </para>
    /// <para>
    /// Deliberately separate from the shared per-turn tool-call budget, which bounds how often the
    /// model may call anything at all and is what keeps read tools in check. This bounds how many
    /// decisions a turn may put in front of the learner, and the answer is one, because a learner
    /// answering two Accept buttons in one exchange cannot tell which one they just agreed to.
    /// </para>
    /// <para>
    /// Enforced before the row is written, never by the prompt. A model instructed to propose once
    /// is a model that usually proposes once.
    /// </para>
    /// </remarks>
    public const int ProposalsPerTurnMax = 1;
}
