namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Stable DOM ids used by the coach workspace for focus management. Centralized so the state
/// service and the components cannot drift apart.
/// </summary>
public static class CoachElementIds
{
    /// <summary>The overlay dialog element.</summary>
    public const string Dialog = "coachWorkspace";

    /// <summary>The workspace heading, referenced by <c>aria-labelledby</c>.</summary>
    public const string Title = "coach-title";

    /// <summary>The plan canvas region, referenced by the toggle's <c>aria-controls</c>.</summary>
    public const string Canvas = "coach-canvas";

    /// <summary>
    /// The single alert/notice card. Exactly one of the mutually exclusive failure and notice
    /// branches in CoachStateNotice is mounted at a time, and it carries this id so the focus
    /// policy has one stable target. There is no second hidden alert region.
    /// </summary>
    public const string Alert = "coach-alert";

    /// <summary>The composer textarea.</summary>
    public const string Composer = "coach-composer";

    /// <summary>
    /// The conversation stream. Carried so the autoscroll interop can address the one element that
    /// grows when a turn arrives, rather than guessing at a selector.
    /// </summary>
    public const string Messages = "coach-messages";

    /// <summary>
    /// The control offered when new messages landed below what the reader is looking at.
    /// </summary>
    public const string JumpToLatest = "coach-jump-to-latest";

    /// <summary>Id of the receipt card for a given receipt.</summary>
    public static string Receipt(string receiptId) => $"coach-receipt-{receiptId}";
}
