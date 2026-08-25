using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// How much ceremony a proposed change demands before it may run.
/// The zero value is Unknown, so a shape the client cannot read never renders an approval control.
/// </summary>
/// <remarks>
/// This mirrors the server's tool risk class, minus the read tier, which never produces a
/// proposal. The client uses it for one decision only: whether the learner's approval is an
/// ordinary Accept or a protected confirmation. It is never used to decide whether something is
/// allowed — that decision belongs to the server and is made again on every approval request.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachWriteRiskClass.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown already means \u201crender no approval control\u201d. This is the member that makes an "
    + "unreadable proposal unactionable rather than mis-channelled.")]
public enum CoachWriteRiskClass
{
    /// <summary>The class could not be read. Render no approval control.</summary>
    Unknown = 0,

    /// <summary>A reversible learner-owned change: explicit acceptance, then execution.</summary>
    WriteSoft,

    /// <summary>A protected change: a one-use confirmation the server issues, then execution.</summary>
    WriteHard
}

/// <summary>
/// Where a proposed change is in its life.
/// The zero value is Unknown, so an unreadable state never shows as applied.
/// </summary>
/// <remarks>
/// The names mirror the server ledger's own states so an operator reading a client log and an
/// operator reading the ledger are looking at the same vocabulary. The client treats every value
/// other than <see cref="Proposed"/> as "no approval control", and only <see cref="Executed"/> as
/// "this happened" — never an HTTP status, and never what the model said in its reply.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachWriteStatus.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown already means \u201chonest unavailable card, never an action\u201d. Only Proposed draws "
    + "approval controls and only Executed claims a change happened, so an unreadable status can do "
    + "neither.")]
public enum CoachWriteStatus
{
    /// <summary>The state could not be read. Show an honest unavailable card, never an action.</summary>
    Unknown = 0,

    /// <summary>Recorded and waiting for the learner. Nothing in learner data has changed.</summary>
    Proposed,

    /// <summary>An approval holds the execution claim. The outcome is not known yet.</summary>
    Executing,

    /// <summary>The change was carried out and has not been reversed.</summary>
    Executed,

    /// <summary>An executed change was reversed inside its undo window.</summary>
    Undone,

    /// <summary>The learner declined. Nothing was written and it can never execute.</summary>
    Rejected,

    /// <summary>The approval window elapsed before the learner answered.</summary>
    Expired,

    /// <summary>The change was closed after a failure and can never run again.</summary>
    Failed
}

/// <summary>
/// What kind of change is being proposed, as a closed set the client can localize.
/// The zero value is Unknown, which renders neutral copy rather than a guess.
/// </summary>
/// <remarks>
/// Deliberately not the server's tool name. A tool name is an internal identifier, it is not
/// translatable, and a client contract may not name one at all — the contract privacy rules refuse
/// any member that names a tool. A closed kind gives the card its heading, its icon, and its
/// screen-reader label in the learner's language, and a kind the client does not recognise falls
/// back to the generic wording instead of printing an internal string at the learner.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachWriteChangeKind.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown already means \u201cneutral copy; never print an internal identifier\u201d, and "
    + "SamWritePresentation.HeadingKey already resolves it to the generic heading.")]
public enum CoachWriteChangeKind
{
    /// <summary>Unrecognised. Use neutral copy; never print an internal identifier.</summary>
    Unknown = 0,

    /// <summary>Add a vocabulary word.</summary>
    VocabularyAdd,

    /// <summary>Change a field on a vocabulary word.</summary>
    VocabularyEdit,

    /// <summary>Attach a vocabulary word to a learning resource.</summary>
    VocabularyLink,

    /// <summary>Remove a vocabulary word.</summary>
    VocabularyRemove,

    /// <summary>Add a skill.</summary>
    SkillAdd,

    /// <summary>Change a field on a skill.</summary>
    SkillEdit,

    /// <summary>Archive a skill. Not a delete: the row and everything referencing it survive.</summary>
    SkillArchive,

    /// <summary>Add a learning resource.</summary>
    ResourceAdd,

    /// <summary>Change a field on a learning resource.</summary>
    ResourceEdit,

    /// <summary>Remove a learning resource.</summary>
    ResourceRemove,

    /// <summary>Change one of the learner's own settings.</summary>
    SettingChange,

    /// <summary>Import a video transcript as a learning resource.</summary>
    VideoImport
}

/// <summary>
/// The kind of thing an executed change touched.
/// The zero value is None, so an unread receipt never claims to point at a row.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachWriteTargetKind.None), WireEnumFallbackKind.SafeZero,
    "None already means \u201cnothing, or nothing was created\u201d, so an unreadable receipt never "
    + "claims to point at a row the client could then offer to open or undo.")]
public enum CoachWriteTargetKind
{
    /// <summary>Nothing, or nothing was created.</summary>
    None = 0,

    /// <summary>A vocabulary word.</summary>
    VocabularyWord,

    /// <summary>A skill.</summary>
    Skill,

    /// <summary>A learning resource.</summary>
    LearningResource,

    /// <summary>A link between a vocabulary word and a learning resource.</summary>
    VocabularyLink,

    /// <summary>One of the learner's own settings.</summary>
    LearnerSetting,

    /// <summary>Today's plan.</summary>
    DailyPlan
}

/// <summary>
/// The authoritative record of a change that ran, as the learner's client sees it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "applied" true. A successful HTTP response says a request was accepted; it
/// does not say a change happened, and the difference matters most exactly when it is least
/// visible — a claim lost mid-flight, a handler that failed after writing, a retry that replayed.
/// The client renders an applied state only when it holds one of these with
/// <see cref="Status"/> of <see cref="CoachWriteStatus.Executed"/>.
/// </para>
/// <para>
/// It carries no arguments, no prior values, no audit rows, and nothing protected. The summary and
/// the lines are the same learner-facing sentences the preview showed, re-stated in the past tense
/// by the handler that did the work.
/// </para>
/// </remarks>
public sealed class CoachWriteReceiptDto
{
    /// <summary>The operation this receipt belongs to.</summary>
    public required string OperationId { get; init; }

    /// <summary>What kind of change ran.</summary>
    public required CoachWriteChangeKind ChangeKind { get; init; }

    /// <summary>How much ceremony the change required.</summary>
    public required CoachWriteRiskClass RiskClass { get; init; }

    /// <summary>Where the operation stands now. Only Executed means the change is in place.</summary>
    public required CoachWriteStatus Status { get; init; }

    /// <summary>What kind of thing was touched.</summary>
    public CoachWriteTargetKind TargetKind { get; init; }

    /// <summary>The affected row's opaque identifier, when one exists.</summary>
    public string? TargetId { get; init; }

    /// <summary>One line describing what changed.</summary>
    public required string Summary { get; init; }

    /// <summary>Bounded detail lines describing what changed, in order.</summary>
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    /// <summary>When the change ran.</summary>
    public required DateTime ExecutedAtUtc { get; init; }

    /// <summary>
    /// True only while an undo is genuinely available: the operation is reversible, it executed,
    /// and its window is still open.
    /// </summary>
    /// <remarks>
    /// The client renders an Undo control from this field and nothing else. Several changes are
    /// honestly irreversible — a removal that cascades, an import whose content may already have
    /// been read — and offering a button that would delete an approximation of what used to exist
    /// is worse than offering none, because the learner believes the original is safe.
    /// </remarks>
    public bool CanUndo { get; init; }

    /// <summary>When the undo window closes. Null when there is no undo.</summary>
    public DateTime? UndoExpiresAtUtc { get; init; }
}

/// <summary>
/// One proposed learner-owned change and everything the client needs to render it truthfully.
/// </summary>
/// <remarks>
/// <para>
/// A proposal is a moment of learner control inside a conversation, so it is placed inside the
/// conversation: <see cref="TurnId"/> and <see cref="MessageId"/> say which exchange it belongs
/// to, and the client renders it there rather than stacking it at the top of the thread. Both are
/// server-assigned, which is what lets a reload rebuild the same card in the same place.
/// </para>
/// <para>
/// What this deliberately does not carry: the arguments the change would apply, any prior values,
/// any protected payload, any audit row, and — above all — the one-use confirmation a protected
/// change needs. That value is minted on a separate authenticated request, returned once, and
/// never written to any durable client state. A shape that could carry it is a shape that will
/// eventually be logged.
/// </para>
/// </remarks>
public sealed class CoachWriteOperationDto
{
    /// <summary>The operation identifier. Opaque, owner-scoped, and the handle every action uses.</summary>
    public required string OperationId { get; init; }

    /// <summary>The conversation the proposal belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// The turn that produced the proposal, so the card renders inside that exchange.
    /// </summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// The message the card anchors to, when the server could pair it with one.
    /// </summary>
    /// <remarks>
    /// Set on the durable history surface, where a message and a proposal can be paired by the
    /// turn that produced both. A live turn may carry a proposal before its messages have
    /// identifiers, so this is null there and <see cref="TurnId"/> does the placing.
    /// </remarks>
    public string? MessageId { get; init; }

    /// <summary>What kind of change this is.</summary>
    public required CoachWriteChangeKind ChangeKind { get; init; }

    /// <summary>How much ceremony this change demands.</summary>
    public required CoachWriteRiskClass RiskClass { get; init; }

    /// <summary>Where the proposal stands. Only Proposed is actionable.</summary>
    public required CoachWriteStatus Status { get; init; }

    /// <summary>
    /// The approval channel: <c>accept</c> for an ordinary change, <c>confirm</c> for a protected
    /// one. Kept as the same literal the server has always sent.
    /// </summary>
    public required string ApprovalMode { get; init; }

    /// <summary>One line describing what would change, or what changed.</summary>
    public required string Summary { get; init; }

    /// <summary>Bounded detail lines, in order.</summary>
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    /// <summary>When the proposal stops being answerable.</summary>
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>
    /// True when this change needs a protected confirmation rather than a plain acceptance.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>
    /// When an outstanding confirmation stops being redeemable. Null until the learner opens the
    /// confirmation step, and null again once it has been spent.
    /// </summary>
    public DateTime? ConfirmationExpiresAtUtc { get; init; }

    /// <summary>
    /// True when the server could reverse this change after it ran. Advisory only: the receipt's
    /// <see cref="CoachWriteReceiptDto.CanUndo"/> is what actually renders an Undo control.
    /// </summary>
    public bool IsReversible { get; init; }

    /// <summary>True when this repeats a proposal already recorded, so nothing was duplicated.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>True when the change is already in place and this is the stored outcome.</summary>
    public bool AlreadyExecuted { get; init; }

    /// <summary>
    /// The authoritative receipt, once the change has run. Null while it is still a proposal.
    /// </summary>
    public CoachWriteReceiptDto? Receipt { get; init; }
}
