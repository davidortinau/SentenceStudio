using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Pure transition rules for the shared coach UI. Kept separate from
/// <see cref="CoachWorkspaceState"/> so the rules can be unit tested without HTTP, a circuit,
/// or a rendered component.
/// </summary>
public static class CoachStateMachine
{
    /// <summary>The presentation breakpoint. 768px is the repository's existing structural
    /// breakpoint (sidebar is <c>d-none d-md-flex</c>; mobile chrome lives in
    /// <c>@media (max-width: 767.98px)</c>). Do not invent a second one.</summary>
    public const int OverlayMinimumViewportWidth = 768;

    /// <summary>Width at which the overlay shows the split chat + plan canvas instead of tabs.</summary>
    public const int SplitCanvasMinimumViewportWidth = 992;

    /// <summary>Seconds after which a running turn shows a secondary "still working" line.</summary>
    public const int StillWorkingThresholdSeconds = 8;

    /// <summary>Seconds after which a running turn offers a Stop affordance.</summary>
    public const int StopAffordanceThresholdSeconds = 12;

    /// <summary>
    /// Chooses the composition from the viewport width measured at entry.
    /// Never platform-sniffs: the same UI runs in a desktop browser, Mac Catalyst, iPad and iPhone,
    /// and a narrowed desktop window must behave like a narrow device.
    /// </summary>
    public static CoachPresentation ChoosePresentation(int viewportWidth) =>
        viewportWidth >= OverlayMinimumViewportWidth ? CoachPresentation.Overlay : CoachPresentation.FullScreen;

    /// <summary>True when the wide split layout applies. Below this the overlay uses tabs.</summary>
    public static bool UsesSplitCanvas(int viewportWidth) => viewportWidth >= SplitCanvasMinimumViewportWidth;

    /// <summary>Maps a completed turn onto a UI state. Precedence is failure, then limits, then writes.</summary>
    public static CoachUiState FromTurn(CoachTurnResponse turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.StopReason == CoachStopReason.SessionExpired || turn.SessionStatus == CoachSessionStatus.Expired)
        {
            return CoachUiState.Expired;
        }

        if (turn.StopReason == CoachStopReason.RateLimit)
        {
            return CoachUiState.Limited;
        }

        // A validated refusal is not an operational failure.
        //
        // Every refusal path on the server carries exactly this shape: Rejected +
        // ValidationFailed + no receipt. It covers the suggestion validator finding no
        // effective change, an unusable model answer, a failed ownership check, and an
        // answer-leak embargo. In all four the coach deliberately declined to act and said so
        // in a Notice message, and nothing was written.
        //
        // Showing the failure alert here was both wrong and harmful: "I could not update
        // Today's Plan" reads as a malfunction rather than a safe no-op, and Try again invites
        // the learner to re-run something that is designed to refuse again. Detection is on the
        // response SHAPE, never on the copy, which the model and the server own.
        if (turn.Status == CoachTurnStatus.Rejected
            && turn.StopReason == CoachStopReason.ValidationFailed
            && turn.ChangeReceipt is null
            && string.IsNullOrWhiteSpace(turn.ClarifyingQuestion))
        {
            // A refusal never silently withdraws an offer the learner has not answered yet.
            return turn.PendingSuggestion is not null
                ? CoachUiState.SuggestionPending
                : CoachUiState.Ready;
        }

        // Status=Failed is the server saying something broke, and outranks everything below —
        // a real failure must never be dressed up as a question.
        if (turn.Status == CoachTurnStatus.Failed)
        {
            return CoachUiState.Failed;
        }

        // Clarification is checked BEFORE Incomplete, and that ordering is the whole point.
        //
        // The server reports an asked clarification as Status=Incomplete with
        // StopReason=ClarificationRequested, because the run genuinely did stop early. But to
        // the learner it is an expected conversational turn, not a failure: an ambiguous reply
        // ("Maybe.") got a focused question back and the pending suggestion was preserved.
        // Checking Incomplete first showed "The coach stopped before finishing. Nothing
        // changed." with Try again / Keep Today's Plan next to the question — misleading, and
        // duplicating the accept/reject actions the learner was being asked to choose between.
        var asksClarification = turn.StopReason == CoachStopReason.ClarificationRequested
            || turn.SessionStatus == CoachSessionStatus.AwaitingClarification
            || !string.IsNullOrWhiteSpace(turn.ClarifyingQuestion);

        // Clarification also outranks a pending suggestion: the suggestion card stays mounted
        // in its pending visual state while the machine reports that the coach asked. Nothing
        // is written on either path.
        if (asksClarification)
        {
            // Never loop. Once the per-session budget is spent the UI presents a binary choice
            // instead of asking again — which is also how the server's "I still could not tell
            // what to change" turn arrives: same stop reason, but no question attached.
            return turn.ClarificationsRemaining <= 0 || string.IsNullOrWhiteSpace(turn.ClarifyingQuestion)
                ? CoachUiState.ClarificationLimitReached
                : CoachUiState.Clarification;
        }

        // A rejection that did not match the safe-refusal shape above — a different stop reason,
        // or one that somehow carries a receipt — is a failure. Checked before the receipt and
        // pending-suggestion branches so it can never be reported as a successful write.
        if (turn.Status == CoachTurnStatus.Rejected)
        {
            return CoachUiState.Failed;
        }

        if (turn.Status == CoachTurnStatus.Incomplete)
        {
            return CoachUiState.Incomplete;
        }

        if (turn.ChangeReceipt is { } receipt)
        {
            return receipt.Revision.Source == CoachRevisionSource.Undo
                ? CoachUiState.Undone
                : CoachUiState.PlanUpdated;
        }

        if (turn.PendingSuggestion is not null)
        {
            return CoachUiState.SuggestionPending;
        }

        return CoachUiState.Ready;
    }

    /// <summary>
    /// Maps a typed coach API problem onto a UI state.
    /// </summary>
    /// <remarks>
    /// Aligned with the landed <c>CoachEndpoints.ToProblem</c> table. Every problem type the
    /// server can emit is handled explicitly; the default is only reached for an unexpected 500
    /// (which carries no <c>type</c>) or a problem type added later.
    /// </remarks>
    public static CoachUiState FromProblem(CoachApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.ProblemType switch
        {
            // 404 band. All four are "there is nothing here" from the server's point of view,
            // but they mean different things to the learner.
            CoachProblemTypes.SessionExpired => CoachUiState.Expired,
            CoachProblemTypes.SessionNotFound => CoachUiState.Expired,
            // The feature was turned off, the learner left the cohort, or Today's Plan is gone
            // mid-session. The workspace must close and the Dashboard entry must reset — not
            // offer "start a new session", which would fail the same way.
            CoachProblemTypes.Unavailable => CoachUiState.SessionDeleted,
            // The named suggestion is no longer the pending one (already accepted, rejected, or
            // superseded). Nothing failed and nothing was written: resolve and carry on.
            CoachProblemTypes.SuggestionNotFound => CoachUiState.Ready,

            // 409 band.
            CoachProblemTypes.PlanVersionConflict => CoachUiState.PlanChangedElsewhere,
            CoachProblemTypes.RunInProgress => CoachUiState.Incomplete,

            // 422 band. Nothing was written in any of these.
            CoachProblemTypes.InvalidTurnInput => CoachUiState.InputTooLong,
            CoachProblemTypes.InvalidConstraint => CoachUiState.Failed,
            CoachProblemTypes.PlanValidationFailed => CoachUiState.Failed,
            // There is no applied, not-yet-undone revision. The receipt's Undo was stale; this is
            // not a failure of Today's Plan, so do not claim one.
            CoachProblemTypes.NothingToUndo => CoachUiState.Ready,

            // 429 / 503 bands.
            CoachProblemTypes.RateLimited => CoachUiState.Limited,
            CoachProblemTypes.ToolFailure => CoachUiState.Incomplete,
            CoachProblemTypes.Timeout => CoachUiState.Incomplete,

            // Durable-history band. These arrive from the /conversations routes. None of them is
            // an operational failure, so none may fall through to the generic Failed alert.
            //
            // A conversation that is missing and a conversation owned by somebody else are the
            // same 404 by design, and both mean this thread is gone for good — the same shape as
            // a deleted session, not an expiry the learner can resume past.
            CoachProblemTypes.ConversationNotFound => CoachUiState.SessionDeleted,
            // The conversation moved underneath this request: renamed, closed, or written by
            // another device. Nothing was lost; re-read and carry on.
            CoachProblemTypes.ConversationStateConflict => CoachUiState.PlanChangedElsewhere,
            // The same operation id arrived with a different payload. The first turn stands, so
            // this is emphatically not a failure of the conversation — do not offer Try again,
            // which would resend the payload that was already refused.
            CoachProblemTypes.IdempotencyConflict => CoachUiState.Ready,
            // A paging cursor the server will not honour: stale, tampered with, or from another
            // conversation. Recovering is a re-read from the top, not an error.
            CoachProblemTypes.InvalidCursor => CoachUiState.Ready,
            // The learner asked for this. Presenting a cancellation as a failure would be telling
            // them their own deliberate action broke something.
            CoachProblemTypes.RunCancelled => CoachUiState.Ready,

            _ => CoachUiState.Failed
        };
    }

    /// <summary>
    /// Canvas auto-open contract: open once per new suggestion or applied revision. If the learner
    /// closes it afterwards it stays closed for that same revision, and only a *new* change opens
    /// it again.
    /// </summary>
    /// <param name="lastAutoOpenKey">The suggestion/revision key the canvas already auto-opened for.</param>
    /// <param name="newKey">The current suggestion/revision key, or null when there is none.</param>
    public static bool ShouldAutoOpenCanvas(string? lastAutoOpenKey, string? newKey) =>
        !string.IsNullOrEmpty(newKey) && !string.Equals(lastAutoOpenKey, newKey, StringComparison.Ordinal);

    /// <summary>
    /// Announce-or-focus policy. Resolves the contradiction between "focus the receipt after a
    /// revision" and "do not double-announce": focus follows the initiating input modality.
    /// A typed action must never yank focus out of the composer.
    /// </summary>
    public static CoachOutcomePolicy OutcomePolicy(CoachInitiator initiator, bool succeeded)
    {
        if (!succeeded)
        {
            // Failures use role="alert" only. Focus moves to the alert card only when the
            // control that started the action was destroyed by the failure render.
            var destroyedControl = initiator is CoachInitiator.SuggestionButton or CoachInitiator.UndoButton;
            return new CoachOutcomePolicy(AnnouncePolitely: false, MoveFocusToReceipt: destroyedControl);
        }

        return initiator switch
        {
            // The tapped button is removed when the card collapses into the receipt, so focus
            // would otherwise fall to <body>. Focusing the receipt reads it: suppress the announce.
            CoachInitiator.SuggestionButton => new CoachOutcomePolicy(false, true),
            CoachInitiator.UndoButton => new CoachOutcomePolicy(false, true),
            // Chips survive the render (aria-pressed updates), so keep focus and announce.
            CoachInitiator.Chip => new CoachOutcomePolicy(true, false),
            CoachInitiator.Composer => new CoachOutcomePolicy(true, false),
            _ => new CoachOutcomePolicy(true, false)
        };
    }

    /// <summary>
    /// Resolves the single action pair a pending suggestion offers.
    /// </summary>
    /// <remarks>
    /// A clarification does not add actions — it re-frames the same binary choice in explicit
    /// terms, so the labels change and the question is shown, but the pair stays one pair.
    /// </remarks>
    /// <param name="state">Current UI state.</param>
    /// <param name="hasPendingSuggestion">Whether a suggestion is awaiting an answer.</param>
    /// <param name="hasClarification">Whether a focused clarification question is available.</param>
    public static CoachSuggestionActions SuggestionActions(
        CoachUiState state,
        bool hasPendingSuggestion,
        bool hasClarification)
    {
        if (!hasPendingSuggestion)
        {
            return new CoachSuggestionActions(false, false, null, null);
        }

        var clarifying = hasClarification
            && state is CoachUiState.Clarification or CoachUiState.ClarificationLimitReached;

        return new CoachSuggestionActions(
            IsVisible: true,
            ShowClarification: clarifying,
            // The clarification restates the choice explicitly ("Yes, update it"); otherwise the
            // offer's own wording is used, which the caller may override with the server label.
            AcceptLabelKey: clarifying ? "Coach_ClarifyYes" : "Coach_Accept",
            RejectLabelKey: "Coach_Reject");
    }

    /// <summary>True when the composer and every constraint affordance must be disabled.</summary>
    public static bool IsBusy(CoachUiState state) => state
        is CoachUiState.Opening
        or CoachUiState.Resuming
        or CoachUiState.LoadingEvidence
        or CoachUiState.Running
        or CoachUiState.Applying
        or CoachUiState.Undoing;

    /// <summary>True when the learner can submit a turn.</summary>
    public static bool CanSubmit(CoachUiState state) => state
        is CoachUiState.Ready
        or CoachUiState.Clarification
        or CoachUiState.SuggestionPending
        or CoachUiState.PlanUpdated
        or CoachUiState.Undone
        or CoachUiState.PlanChangedElsewhere
        or CoachUiState.Incomplete
        or CoachUiState.ClarificationLimitReached
        or CoachUiState.Failed;

    /// <summary>
    /// True when the state is terminal for this session: the workspace can no longer submit turns
    /// and must offer a fresh start rather than a retry.
    /// </summary>
    public static bool IsTerminal(CoachUiState state) => state
        is CoachUiState.Expired
        or CoachUiState.SessionDeleted;

    /// <summary>
    /// Resource key announced (politely, or as an alert for failures) when the machine enters a
    /// state. Returning a key rather than text keeps this type free of culture state.
    /// </summary>
    public static string? AnnouncementKey(CoachUiState state) => state switch
    {
        CoachUiState.Resuming => "Coach_AnnounceResumed",
        CoachUiState.LoadingEvidence => "Coach_StageReading",
        CoachUiState.Running => "Coach_AnnounceWorking",
        CoachUiState.Applying => "Coach_StageApplying",
        CoachUiState.SuggestionPending => "Coach_StatusSuggested",
        CoachUiState.PlanUpdated => "Coach_StatusUpdated",
        CoachUiState.Undoing => "Coach_AnnounceUndoing",
        CoachUiState.Undone => "Coach_ReceiptUndone",
        CoachUiState.Clarification => "Coach_AnnounceClarification",
        CoachUiState.ClarificationLimitReached => "Coach_ClarificationLimit",
        CoachUiState.PlanChangedElsewhere => "Coach_StatusOutOfDate",
        CoachUiState.Incomplete => "Coach_Incomplete",
        CoachUiState.Offline => "Coach_Offline",
        CoachUiState.Limited => "Coach_LimitedShort",
        CoachUiState.Failed => "Coach_Failed",
        CoachUiState.Expired => "Coach_Expired",
        CoachUiState.SessionDeleted => "Coach_AnnounceDeleted",
        // Deliberately NOT Coach_InputTooLong: that string carries a {0} character-limit
        // argument and the live region renders announcement keys with no arguments, so it
        // would be read aloud as a literal brace.
        CoachUiState.InputTooLong => "Coach_InputRejected",
        _ => null
    };
}
