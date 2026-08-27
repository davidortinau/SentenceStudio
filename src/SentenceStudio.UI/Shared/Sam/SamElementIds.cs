namespace SentenceStudio.WebUI.Shared.Sam;

/// <summary>
/// DOM element IDs for the Sam overlay surface. Keeps accessibility anchors
/// (aria-controls, focus targets) in one place.
/// </summary>
public static class SamElementIds
{
    public const string Fab = "sam-fab";
    public const string Panel = "sam-panel";
    public const string PanelTitle = "sam-panel-title";

    // ------------------------------------------------------------------- panel size controls
    //
    // Named for what they do rather than for where they sit, because which of them is rendered
    // depends on the panel's current size: expand and full-screen are offered at the smaller
    // sizes, restore replaces both while the panel covers the viewport. A test — or a learner's
    // focus after a size change — addresses the action, and the action does not move.

    /// <summary>Grows a compact panel to the expanded size.</summary>
    public const string PanelExpand = "sam-panel-expand";

    /// <summary>Returns an expanded panel to the compact size.</summary>
    public const string PanelCompact = "sam-panel-compact";

    /// <summary>Grows the panel to cover the viewport, in place.</summary>
    public const string PanelFullScreen = "sam-panel-fullscreen";

    /// <summary>Leaves full screen for the size the panel had before it.</summary>
    public const string PanelRestore = "sam-panel-restore";

    /// <summary>Closes the panel back to the entry control.</summary>
    public const string PanelClose = "sam-panel-close";

    // ---------------------------------------------------------------- proposed changes
    //
    // One card per operation, so every id is derived from the operation identifier rather than
    // from a position in the thread. A position moves when older messages load; an operation
    // identifier does not, which is what lets a test — or a focus restore after an approval —
    // address the same card before and after the list around it changes.
    //
    // The identifier is a server handle the learner never sees. It is not a secret and reading it
    // grants nothing: every route it names re-checks ownership on the authenticated request. The
    // one-use confirmation never appears in an id, an attribute, or the DOM at all.

    /// <summary>The proposal or receipt card for one operation.</summary>
    public static string WriteCard(string operationId) => $"sam-write-{operationId}";

    /// <summary>The card's heading, referenced by its <c>aria-labelledby</c>.</summary>
    public static string WriteCardTitle(string operationId) => $"sam-write-{operationId}-title";

    /// <summary>The card's summary line, referenced by the actions' <c>aria-describedby</c>.</summary>
    public static string WriteCardSummary(string operationId) => $"sam-write-{operationId}-summary";

    /// <summary>The Apply control on a reversible proposal.</summary>
    public static string WriteAccept(string operationId) => $"sam-write-{operationId}-accept";

    /// <summary>The decline control, present for both risk classes.</summary>
    public static string WriteDecline(string operationId) => $"sam-write-{operationId}-decline";

    /// <summary>The control that opens the confirmation step for a protected change.</summary>
    public static string WriteReview(string operationId) => $"sam-write-{operationId}-review";

    /// <summary>The confirmation step itself.</summary>
    public static string WriteConfirmStep(string operationId) => $"sam-write-{operationId}-confirm-step";

    /// <summary>The control that carries out a protected change.</summary>
    public static string WriteConfirm(string operationId) => $"sam-write-{operationId}-confirm";

    /// <summary>The control that leaves the confirmation step without approving.</summary>
    public static string WriteConfirmCancel(string operationId) => $"sam-write-{operationId}-confirm-cancel";

    /// <summary>The Undo control on a completed, reversible change.</summary>
    public static string WriteUndo(string operationId) => $"sam-write-{operationId}-undo";

    /// <summary>The control that re-reads a change's state.</summary>
    public static string WriteRefresh(string operationId) => $"sam-write-{operationId}-refresh";

    /// <summary>The card's refusal message, referenced by the actions' <c>aria-describedby</c>.</summary>
    public static string WriteError(string operationId) => $"sam-write-{operationId}-error";
}
