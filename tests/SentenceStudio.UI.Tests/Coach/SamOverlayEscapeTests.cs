using FluentAssertions;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Escape precedence between the overlay and a protected change's confirmation step.
/// </summary>
/// <remarks>
/// The overlay collapses on Escape (`SAM-A11Y-01`) and the confirmation step cancels on Escape
/// (`§31.19`). Both handlers are live at once, and the outer one used to win: browser E2E on
/// 2026-08-19 opened "Review and confirm" on a skill archive, pressed Escape, and the whole panel
/// collapsed — taking the confirmation step with it and leaving no visible way back into the step.
/// The learner's data was never at risk; the contract was simply not met.
/// </remarks>
public class SamOverlayEscapeTests
{
    [Theory]
    [InlineData(SamOverlayVisualState.Compact)]
    [InlineData(SamOverlayVisualState.Expanded)]
    public void An_open_panel_collapses_when_nothing_inside_it_claims_escape(
        SamOverlayVisualState state)
    {
        SamOverlayEscape.Resolve(state, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Collapse);
    }

    [Theory]
    [InlineData(SamOverlayVisualState.Compact)]
    [InlineData(SamOverlayVisualState.Expanded)]
    public void A_confirmation_step_keeps_the_panel_open(SamOverlayVisualState state)
    {
        SamOverlayEscape.Resolve(state, isConfirmingWrite: true, isReportPanelOpen: false)
            .Should().Be(
                SamOverlayEscapeAction.DeferToPanelContent,
                "the innermost dismissable surface answers Escape first");
    }

    [Fact]
    public void A_collapsed_overlay_ignores_escape()
    {
        SamOverlayEscape.Resolve(SamOverlayVisualState.Collapsed, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Ignore);
    }

    [Fact]
    public void A_collapsed_overlay_ignores_escape_even_if_a_confirmation_is_somehow_flagged()
    {
        // A confirmation step cannot render inside a panel that is not rendered. Answering Ignore
        // rather than DeferToPanelContent keeps the collapsed state from depending on write state
        // it cannot see.
        SamOverlayEscape.Resolve(SamOverlayVisualState.Collapsed, isConfirmingWrite: true, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Ignore);
    }

    [Fact]
    public void The_second_escape_collapses_after_the_confirmation_is_cancelled()
    {
        var state = SamOverlayVisualState.Expanded;

        SamOverlayEscape.Resolve(state, isConfirmingWrite: true, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.DeferToPanelContent);

        // The card's own handler cancelled the confirmation; the next keypress sees no inner claim.
        SamOverlayEscape.Resolve(state, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Collapse);
    }

    // ================================================================ full screen

    /// <summary>
    /// Full screen is a state the learner entered on purpose, so Escape undoes that step rather
    /// than skipping past it and closing the panel they were reading.
    /// </summary>
    [Fact]
    public void A_full_screen_panel_restores_its_previous_size_rather_than_closing()
    {
        SamOverlayEscape.Resolve(SamOverlayVisualState.FullScreen, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Restore);
    }

    [Fact]
    public void A_confirmation_step_still_wins_over_leaving_full_screen()
    {
        SamOverlayEscape.Resolve(SamOverlayVisualState.FullScreen, isConfirmingWrite: true, isReportPanelOpen: false)
            .Should().Be(
                SamOverlayEscapeAction.DeferToPanelContent,
                "innermost first: the confirmation is inside the panel, whatever size it is");
    }

    /// <summary>
    /// The whole order, in one place: confirmation, then full screen, then the panel.
    /// </summary>
    [Fact]
    public void Escape_undoes_one_thing_per_press_in_the_order_they_were_opened()
    {
        // Confirmation step open inside a maximized panel.
        SamOverlayEscape.Resolve(SamOverlayVisualState.FullScreen, isConfirmingWrite: true, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.DeferToPanelContent);

        // The card cancelled itself; the panel is still maximized.
        SamOverlayEscape.Resolve(SamOverlayVisualState.FullScreen, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Restore);

        // Restored to the size it had.
        SamOverlayEscape.Resolve(SamOverlayVisualState.Expanded, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Collapse);

        // Collapsed: nothing left to dismiss.
        SamOverlayEscape.Resolve(SamOverlayVisualState.Collapsed, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Ignore);
    }

    // ============================================================= report panel

    /// <summary>
    /// A report panel is the third surface that can be open inside the overlay, and it answers
    /// Escape for itself.
    /// </summary>
    /// <remarks>
    /// Zoe's 2026-08-20 review: with a report panel open, Escape closed the panel AND collapsed the
    /// overlay in one press — two layers dismissed by one key. The panel is inline in the
    /// conversation and does not take focus, so unlike the confirmation step it cannot rely on
    /// being the focused surface; the resolver has to be told about it explicitly.
    /// </remarks>
    [Theory]
    [InlineData(SamOverlayVisualState.Compact)]
    [InlineData(SamOverlayVisualState.Expanded)]
    public void An_open_report_panel_keeps_the_panel_open(SamOverlayVisualState state)
    {
        SamOverlayEscape.Resolve(state, isConfirmingWrite: false, isReportPanelOpen: true)
            .Should().Be(
                SamOverlayEscapeAction.DeferToPanelContent,
                "the innermost dismissable surface answers Escape first");
    }

    [Fact]
    public void An_open_report_panel_still_wins_over_leaving_full_screen()
    {
        SamOverlayEscape.Resolve(SamOverlayVisualState.FullScreen, isConfirmingWrite: false, isReportPanelOpen: true)
            .Should().Be(
                SamOverlayEscapeAction.DeferToPanelContent,
                "innermost first: the panel is inside the overlay, whatever size it is");
    }

    [Fact]
    public void A_collapsed_overlay_ignores_escape_even_if_a_report_panel_is_somehow_flagged()
    {
        // A report panel cannot render inside a conversation that is not rendered. Answering
        // Ignore rather than DeferToPanelContent keeps the collapsed state from depending on inner
        // state it cannot see — the same rule the confirmation step follows.
        SamOverlayEscape.Resolve(SamOverlayVisualState.Collapsed, isConfirmingWrite: false, isReportPanelOpen: true)
            .Should().Be(SamOverlayEscapeAction.Ignore);
    }

    [Fact]
    public void A_confirmation_and_a_report_panel_together_still_defer_once()
    {
        // Both inner surfaces claim Escape, and the answer is the same either way: the overlay
        // stands aside. Which of the two closes is decided inside the panel, not here.
        SamOverlayEscape.Resolve(SamOverlayVisualState.Expanded, isConfirmingWrite: true, isReportPanelOpen: true)
            .Should().Be(SamOverlayEscapeAction.DeferToPanelContent);
    }

    /// <summary>
    /// The whole order with a report panel in it: one press, one layer.
    /// </summary>
    [Fact]
    public void Escape_undoes_the_report_panel_before_the_overlay()
    {
        // Report panel open inside a maximized overlay.
        SamOverlayEscape.Resolve(SamOverlayVisualState.FullScreen, isConfirmingWrite: false, isReportPanelOpen: true)
            .Should().Be(SamOverlayEscapeAction.DeferToPanelContent);

        // The control closed its own panel; the overlay is still maximized.
        SamOverlayEscape.Resolve(SamOverlayVisualState.FullScreen, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Restore);

        // Restored to the size it had.
        SamOverlayEscape.Resolve(SamOverlayVisualState.Expanded, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Collapse);

        // Collapsed: nothing left to dismiss.
        SamOverlayEscape.Resolve(SamOverlayVisualState.Collapsed, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Ignore);
    }
}
