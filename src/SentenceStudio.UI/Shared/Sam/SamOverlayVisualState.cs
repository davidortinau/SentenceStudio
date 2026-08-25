namespace SentenceStudio.WebUI.Shared.Sam;

/// <summary>
/// Visual states for the Sam persistent overlay.
/// Collapsed = FAB only; Compact = narrow panel (mobile); Expanded = wider panel (desktop);
/// FullScreen = the panel covers the viewport in place.
/// </summary>
/// <remarks>
/// Full screen is a size of this overlay, not a different surface.
/// <para>
/// It used to be a navigation to <c>/coach</c>, and on any viewport at or above 768px that route
/// measures itself on first render and redirects straight back to the dashboard
/// (<c>Coach.razor</c> → <c>CoachStateMachine.ChoosePresentation</c>) because the overlay is the
/// correct composition at that width. The learner saw the coach page paint and vanish, and landed
/// back on the dashboard with the panel collapsed — the "flashes then disappears" report. Growing
/// in place has no such round trip: the conversation, the composer draft, the scroll position and
/// focus are the same DOM before and after.
/// </para>
/// </remarks>
public enum SamOverlayVisualState
{
    Collapsed,
    Compact,
    Expanded,
    FullScreen
}

/// <summary>What the overlay should do with an Escape keypress.</summary>
public enum SamOverlayEscapeAction
{
    /// <summary>Nothing is open that Escape dismisses.</summary>
    Ignore,

    /// <summary>
    /// A dismissable surface inside the panel owns this Escape; the overlay must not collapse.
    /// </summary>
    DeferToPanelContent,

    /// <summary>
    /// Leave full screen and go back to the size the panel had before, without closing it.
    /// </summary>
    Restore,

    /// <summary>Collapse the panel back to the FAB.</summary>
    Collapse
}

/// <summary>
/// Decides which surface an Escape keypress belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Escape is claimed by two contracts at once: the overlay collapses on Escape, and a protected
/// change's confirmation step — an alert dialog that has taken focus — cancels on Escape. Both
/// handlers are live at the same moment, and before this the outer one won: a learner reading the
/// confirmation and pressing Escape lost the whole panel, along with the only visible way back to
/// the step they were in. Found in browser E2E on 2026-08-19.
/// </para>
/// <para>
/// Innermost first is the ordinary rule for nested dismissables, so the confirmation step gets the
/// keypress and the next Escape collapses the panel. The step's own handler does the cancelling;
/// this only decides who stands down.
/// </para>
/// <para>
/// Full screen sits between the two: it is a state the learner entered on purpose and can leave
/// without losing the conversation, so Escape undoes that step rather than skipping past it to
/// close everything. One Escape per thing that was opened, in the order it was opened.
/// </para>
/// <para>
/// An inline report panel is the third inner claim, added on 2026-08-21. It is not a dialog and
/// does not take focus, so a press can reach this resolver while the panel is open — the host
/// stands down and asks the panel to close, which keeps the press spending exactly one layer.
/// </para>
/// </remarks>
public static class SamOverlayEscape
{
    /// <param name="visualState">The overlay's current visual state.</param>
    /// <param name="isConfirmingWrite">
    /// True while a write proposal's confirmation step is open inside the panel.
    /// </param>
    /// <param name="isReportPanelOpen">
    /// True while an inline report panel is open inside the conversation.
    /// </param>
    /// <remarks>
    /// Both inner claims are required arguments rather than defaulted ones. A default would let a
    /// new call site collapse the overlay out from under an open inner surface by simply not
    /// mentioning it, which is the defect this type exists to prevent.
    /// </remarks>
    public static SamOverlayEscapeAction Resolve(
        SamOverlayVisualState visualState, bool isConfirmingWrite, bool isReportPanelOpen)
    {
        if (visualState == SamOverlayVisualState.Collapsed)
        {
            // Nothing is showing. A confirmation step cannot be open inside a panel that is not
            // rendered, so this answers before the inner check rather than after it.
            return SamOverlayEscapeAction.Ignore;
        }

        if (isConfirmingWrite || isReportPanelOpen)
        {
            return SamOverlayEscapeAction.DeferToPanelContent;
        }

        return visualState == SamOverlayVisualState.FullScreen
            ? SamOverlayEscapeAction.Restore
            : SamOverlayEscapeAction.Collapse;
    }
}
