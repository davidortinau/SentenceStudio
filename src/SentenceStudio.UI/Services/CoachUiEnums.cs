namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Which composition of the shared coach feature is currently mounted.
/// Chosen once by viewport width at entry, never by platform sniffing.
/// </summary>
public enum CoachPresentation
{
    /// <summary>No presentation chosen yet.</summary>
    Unknown = 0,

    /// <summary>Wide/tablet: modal collaboration workspace hosted in MainLayout.</summary>
    Overlay,

    /// <summary>Narrow: the full-screen <c>/coach</c> route using the canonical activity shell.</summary>
    FullScreen
}

/// <summary>Which pane the full-screen composition is showing.</summary>
public enum CoachPane
{
    Coach = 0,
    Plan
}

/// <summary>
/// The complete shared coach UI state. Every composition (wide split, tablet tabs, mobile panes)
/// renders this one machine.
/// </summary>
public enum CoachUiState
{
    /// <summary>Workspace mounting, no session yet.</summary>
    Opening = 0,

    /// <summary>Rehydrating an existing unexpired session.</summary>
    Resuming,

    /// <summary>Reading trusted learning evidence before the first turn.</summary>
    LoadingEvidence,

    /// <summary>Idle and accepting input.</summary>
    Ready,

    /// <summary>A turn is in flight.</summary>
    Running,

    /// <summary>The coach asked a focused clarification. Nothing was written.</summary>
    Clarification,

    /// <summary>A suggestion with a read-only preview is waiting for explicit acceptance.</summary>
    SuggestionPending,

    /// <summary>A validated change is being written to Today's Plan.</summary>
    Applying,

    /// <summary>Today's Plan was updated. A receipt with Undo is available.</summary>
    PlanUpdated,

    /// <summary>An undo is being written.</summary>
    Undoing,

    /// <summary>The latest revision was undone. There is no redo in v1.</summary>
    Undone,

    /// <summary>The server rejected a stale plan version: the plan changed on another device.</summary>
    PlanChangedElsewhere,

    /// <summary>The run stopped on a limit or tool failure before completing. Nothing was written.</summary>
    Incomplete,

    /// <summary>The device or API is unreachable. Today's Plan is unchanged.</summary>
    Offline,

    /// <summary>The learner reached the configured coach run limit.</summary>
    Limited,

    /// <summary>The composer draft exceeds the per-turn character limit.</summary>
    InputTooLong,

    /// <summary>The per-session clarification budget is spent; offer a binary choice instead of looping.</summary>
    ClarificationLimitReached,

    /// <summary>The turn failed. Nothing changed.</summary>
    Failed,

    /// <summary>The coach session expired. Today's Plan is unchanged.</summary>
    Expired,

    /// <summary>Coach history was deleted; the workspace closes and the entry point resets.</summary>
    SessionDeleted
}

/// <summary>
/// What input modality started the action. Drives the announce-or-focus policy: a typed action
/// keeps focus in the composer and announces politely, while a tapped action whose control is
/// destroyed moves focus to the resulting receipt and suppresses the announcement so the learner
/// does not hear it twice.
/// </summary>
public enum CoachInitiator
{
    /// <summary>Not learner-initiated (resume, evidence load, background refresh).</summary>
    System = 0,

    /// <summary>Typed into the composer.</summary>
    Composer,

    /// <summary>Tapped a constraint chip.</summary>
    Chip,

    /// <summary>Tapped Include it / Not now on a suggestion card.</summary>
    SuggestionButton,

    /// <summary>Tapped Undo.</summary>
    UndoButton
}

/// <summary>
/// A destructive action waiting for explicit learner confirmation.
/// </summary>
/// <remarks>
/// Only irreversible operations appear here. Stopping an in-flight model turn is NOT
/// destructive — nothing is written and Today's Plan is untouched — so Stop acts immediately
/// and is deliberately absent from this enum. Deleting coach history is irreversible, so it is
/// gated.
/// </remarks>
public enum CoachConfirmation
{
    /// <summary>Nothing is awaiting confirmation.</summary>
    None = 0,

    /// <summary>End the coach session, which deletes its conversation and pending suggestion.</summary>
    EndSession
}

/// <summary>
/// The one consequential action pair a pending suggestion offers.
/// </summary>
/// <remarks>
/// A suggestion always presents exactly two consequential actions — accept and decline — no
/// matter how many times the coach has had to ask. When an ambiguous reply produced a
/// clarification the card previously rendered its base pair AND a second identical pair below
/// the question, giving the learner four buttons for a binary choice and duplicating the
/// accessible action set (LC-SUG-01 / LC-AMB-01).
/// </remarks>
/// <param name="IsVisible">False when there is no pending suggestion to act on.</param>
/// <param name="ShowClarification">True when the focused question is rendered above the actions.</param>
/// <param name="AcceptLabelKey">Resource key for the accept action.</param>
/// <param name="RejectLabelKey">Resource key for the decline action.</param>
public readonly record struct CoachSuggestionActions(
    bool IsVisible,
    bool ShowClarification,
    string? AcceptLabelKey,
    string? RejectLabelKey)
{
    /// <summary>
    /// How many consequential actions the card exposes. The contract is two, always: one accept
    /// and one decline, never a second pair.
    /// </summary>
    public int ConsequentialActionCount => IsVisible ? 2 : 0;
}

/// <summary>How the UI should surface the outcome of a completed action.</summary>
/// <param name="AnnouncePolitely">Write the outcome to the single polite live region.</param>
/// <param name="MoveFocusToReceipt">Move focus to the receipt (or alert) element.</param>
public readonly record struct CoachOutcomePolicy(bool AnnouncePolitely, bool MoveFocusToReceipt);
