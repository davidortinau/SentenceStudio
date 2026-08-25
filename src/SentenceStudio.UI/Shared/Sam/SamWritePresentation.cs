using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.WebUI.Shared.Sam;

/// <summary>
/// What a proposal card is showing right now.
/// The zero value is <see cref="Unreadable"/>, so a state nothing computed offers no controls.
/// </summary>
public enum SamWriteStage
{
    /// <summary>The client cannot read this change. Say so; offer nothing.</summary>
    Unreadable = 0,

    /// <summary>Waiting for the learner. Nothing has changed.</summary>
    Proposed,

    /// <summary>A protected change with its confirmation step open.</summary>
    ConfirmationRequired,

    /// <summary>Still waiting on the learner, but no longer the change they can act on.</summary>
    /// <remarks>
    /// Reachable across turns, not within one: a turn records one proposal, but a learner who
    /// leaves one unanswered and asks for something else has two open proposals in the thread and
    /// only the newest is actionable.
    /// </remarks>
    Superseded,

    /// <summary>The window closed before it was answered.</summary>
    Expired,

    /// <summary>
    /// The server could not tell us what state this change is in.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Expired"/> on purpose. A refused read is not a verdict; saying
    /// "expired" because a request answered 404 puts words in the server's mouth, and the learner
    /// reads it as a decision that was made rather than a fact we do not have.
    /// </remarks>
    Unavailable,

    /// <summary>The server holds the execution claim and the outcome is not known.</summary>
    InDoubt,

    /// <summary>The change was carried out.</summary>
    Applied,

    /// <summary>The change was carried out and then reversed.</summary>
    Undone,

    /// <summary>The learner declined.</summary>
    Declined,

    /// <summary>The change was closed after a failure and left no effect.</summary>
    Failed
}

/// <summary>
/// Turns one proposal's authoritative state into the stage the card renders and the controls it
/// is allowed to offer.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the markup so the decisions can be tested without rendering anything, and so
/// there is exactly one place that answers "may this card offer an Accept button". A control
/// whose visibility is decided inline in a template is a control whose rule is re-derived every
/// time somebody edits the template.
/// </para>
/// <para>
/// Every method fails towards showing less. An unreadable state offers nothing; a state the
/// server has not confirmed offers nothing; an Undo appears only when the server's own receipt
/// says the reversal is available and its window is still open.
/// </para>
/// </remarks>
public static class SamWritePresentation
{
    /// <summary>Decides which stage a card is at.</summary>
    /// <param name="operation">The authoritative state, as the server last reported it.</param>
    /// <param name="isActionable">
    /// True when this is the one change the learner may act on. False for an older proposal that
    /// a newer one has superseded, which still renders as a record of what was offered.
    /// </param>
    /// <param name="isConfirming">True when the confirmation step for this change is open.</param>
    /// <param name="utcNow">The current instant, supplied so the decision is testable.</param>
    /// <param name="isUnavailable">
    /// True when the server has answered not-found for this change. The card is kept so the
    /// explanation has somewhere to appear, but it offers nothing and points at asking again.
    /// </param>
    public static SamWriteStage Stage(
        CoachWriteOperationDto? operation,
        bool isActionable,
        bool isConfirming,
        DateTime utcNow,
        bool isUnavailable = false)
    {
        if (operation is null || !IsWellFormed(operation))
        {
            return SamWriteStage.Unreadable;
        }

        // A read that failed is not a state transition. A change the server already told us was
        // applied, reversed, or declined stays what it was told to be: those outcomes are facts
        // about the learner's data, and a later 404 — a swept row, a deploy, a network answer we
        // did not expect — is a fact about the request, not about the change. Relabelling them
        // Expired would report a verdict nobody reached. Everything still in flight genuinely is
        // unknown, and says so.
        if (isUnavailable)
        {
            return operation.Status switch
            {
                CoachWriteStatus.Executed => SamWriteStage.Applied,
                CoachWriteStatus.Undone => SamWriteStage.Undone,
                CoachWriteStatus.Rejected => SamWriteStage.Declined,
                CoachWriteStatus.Expired => SamWriteStage.Expired,
                CoachWriteStatus.Failed => SamWriteStage.Failed,
                _ => SamWriteStage.Unavailable
            };
        }

        return operation.Status switch
        {
            CoachWriteStatus.Executed => SamWriteStage.Applied,
            CoachWriteStatus.Undone => SamWriteStage.Undone,
            CoachWriteStatus.Rejected => SamWriteStage.Declined,
            CoachWriteStatus.Expired => SamWriteStage.Expired,
            CoachWriteStatus.Failed => SamWriteStage.Failed,
            CoachWriteStatus.Executing => SamWriteStage.InDoubt,

            // A proposal whose own window has closed is expired whatever the stored status says.
            // The server has not swept the row yet, and telling the learner it is still waiting
            // would invite a press that can only be refused.
            CoachWriteStatus.Proposed when operation.ExpiresAtUtc <= utcNow => SamWriteStage.Expired,
            CoachWriteStatus.Proposed when !isActionable => SamWriteStage.Superseded,
            CoachWriteStatus.Proposed when isConfirming => SamWriteStage.ConfirmationRequired,
            CoachWriteStatus.Proposed => SamWriteStage.Proposed,

            _ => SamWriteStage.Unreadable
        };
    }

    /// <summary>
    /// True when the state is coherent enough to render controls for.
    /// </summary>
    /// <remarks>
    /// Mirrors the same rule the workspace applies before it will act, so a control that renders
    /// is a control the state service will honour and one that does not render is one it would
    /// have refused anyway.
    /// </remarks>
    public static bool IsWellFormed(CoachWriteOperationDto operation) =>
        operation.OperationId.Length > 0
        && operation.Status != CoachWriteStatus.Unknown
        && operation.RiskClass != CoachWriteRiskClass.Unknown
        && (operation.RiskClass == CoachWriteRiskClass.WriteHard) == operation.RequiresConfirmation
        && string.Equals(
            operation.ApprovalMode,
            operation.RequiresConfirmation ? "confirm" : "accept",
            StringComparison.Ordinal);

    /// <summary>The resource key naming what kind of change this is.</summary>
    public static string HeadingKey(CoachWriteChangeKind kind) => kind switch
    {
        CoachWriteChangeKind.VocabularyAdd => "Coach_WriteKindVocabularyAdd",
        CoachWriteChangeKind.VocabularyEdit => "Coach_WriteKindVocabularyEdit",
        CoachWriteChangeKind.VocabularyLink => "Coach_WriteKindVocabularyLink",
        CoachWriteChangeKind.VocabularyRemove => "Coach_WriteKindVocabularyRemove",
        CoachWriteChangeKind.SkillAdd => "Coach_WriteKindSkillAdd",
        CoachWriteChangeKind.SkillEdit => "Coach_WriteKindSkillEdit",
        CoachWriteChangeKind.SkillArchive => "Coach_WriteKindSkillArchive",
        CoachWriteChangeKind.ResourceAdd => "Coach_WriteKindResourceAdd",
        CoachWriteChangeKind.ResourceEdit => "Coach_WriteKindResourceEdit",
        CoachWriteChangeKind.ResourceRemove => "Coach_WriteKindResourceRemove",
        CoachWriteChangeKind.SettingChange => "Coach_WriteKindSettingChange",
        CoachWriteChangeKind.VideoImport => "Coach_WriteKindVideoImport",
        _ => "Coach_WriteKindUnknown"
    };

    /// <summary>The resource key naming the stage, for the badge and the accessible status.</summary>
    public static string StateKey(SamWriteStage stage) => stage switch
    {
        SamWriteStage.Proposed => "Coach_WriteStateProposed",
        SamWriteStage.ConfirmationRequired => "Coach_WriteStateProtected",
        SamWriteStage.Applied => "Coach_WriteStateApplied",
        SamWriteStage.Undone => "Coach_WriteStateUndone",
        SamWriteStage.Declined => "Coach_WriteStateDeclined",
        SamWriteStage.Expired => "Coach_WriteStateExpired",
        SamWriteStage.Superseded => "Coach_WriteStateExpired",
        SamWriteStage.Unavailable => "Coach_WriteStateUnavailable",
        SamWriteStage.InDoubt => "Coach_WriteStateInDoubt",
        SamWriteStage.Failed => "Coach_WriteStateFailed",
        _ => "Coach_WriteStateUnreadable"
    };

    /// <summary>The modifier the card's CSS class carries, so a stage is styleable and greppable.</summary>
    public static string StageCss(SamWriteStage stage) => stage switch
    {
        SamWriteStage.Proposed => "proposed",
        SamWriteStage.ConfirmationRequired => "confirming",
        SamWriteStage.Applied => "applied",
        SamWriteStage.Undone => "undone",
        SamWriteStage.Declined => "declined",
        SamWriteStage.Expired => "expired",
        SamWriteStage.Superseded => "expired",
        SamWriteStage.Unavailable => "unavailable",
        SamWriteStage.InDoubt => "in-doubt",
        SamWriteStage.Failed => "failed",
        _ => "unreadable"
    };

    /// <summary>True when the card offers a single Accept for a reversible change.</summary>
    public static bool ShowsAccept(CoachWriteOperationDto operation, SamWriteStage stage) =>
        stage == SamWriteStage.Proposed && !operation.RequiresConfirmation;

    /// <summary>True when the card offers the step that opens a protected confirmation.</summary>
    public static bool ShowsReview(CoachWriteOperationDto operation, SamWriteStage stage) =>
        stage == SamWriteStage.Proposed && operation.RequiresConfirmation;

    /// <summary>True when the card offers a decline. Both risk classes can be declined.</summary>
    public static bool ShowsDecline(SamWriteStage stage) =>
        stage is SamWriteStage.Proposed or SamWriteStage.ConfirmationRequired;

    /// <summary>
    /// True when a reversal is genuinely available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions, and the server supplies two of them. The receipt has to say the reversal
    /// exists (<c>CanUndo</c>) and has to give the instant it closes; both are the server's own
    /// values, carried on the receipt it wrote. The client only compares that instant to the
    /// clock, and only ever to take the button away — a card left open on a desk past its window
    /// stops offering a press that can now only be refused. The clock is never used to grant an
    /// Undo the server did not offer, so a skewed device shows less than it could and never more
    /// than it should.
    /// </para>
    /// <para>
    /// A change whose state we could not re-read offers nothing either, whatever it last said. The
    /// last thing the server told us was that it had been applied; we no longer have grounds to
    /// promise it can be taken back.
    /// </para>
    /// </remarks>
    public static bool ShowsUndo(
        CoachWriteOperationDto operation,
        SamWriteStage stage,
        DateTime utcNow,
        bool isUnavailable = false) =>
        !isUnavailable
        && stage == SamWriteStage.Applied
        && operation.Receipt is { CanUndo: true, UndoExpiresAtUtc: { } closesAt }
        && closesAt > utcNow;

    /// <summary>True when the card offers a re-read because the outcome is genuinely unknown.</summary>
    public static bool ShowsRefresh(SamWriteStage stage) => stage == SamWriteStage.InDoubt;

    /// <summary>
    /// True when the card should warn that the change cannot be taken back.
    /// </summary>
    /// <remarks>
    /// Shown before approval, never after: once a change has run, the receipt's own presence or
    /// absence of an Undo is the honest statement, and repeating the warning would read as a
    /// reproach.
    /// </remarks>
    public static bool ShowsIrreversibleWarning(CoachWriteOperationDto operation, SamWriteStage stage) =>
        stage is SamWriteStage.Proposed or SamWriteStage.ConfirmationRequired
        && !operation.IsReversible;

    /// <summary>True when the card should say the change can be taken back afterwards.</summary>
    public static bool ShowsReversibleNote(CoachWriteOperationDto operation, SamWriteStage stage) =>
        stage is SamWriteStage.Proposed or SamWriteStage.ConfirmationRequired
        && operation.IsReversible;
}
