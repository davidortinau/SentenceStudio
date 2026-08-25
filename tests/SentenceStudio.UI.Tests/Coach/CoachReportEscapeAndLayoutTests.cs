using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Escape closes exactly one layer, and the panel it closes is rendered below the action row rather
/// than wedged into it.
/// </summary>
/// <remarks>
/// Both of these were rejected on the previous revision and both are structural, so both are tested
/// against the real components rather than a hand-built markup sample.
///
/// The Escape defect was that one press closed the panel *and* collapsed the overlay behind it,
/// because the overlay's document handler and the panel's own handler both ran. The fix has two
/// halves that must not disagree: the resolver stands down when a panel is open, and the script
/// declines to report a press that started inside the panel's own surface at all. A test that only
/// covered one half would pass while the other regressed.
///
/// The layout defect was that the panel rendered as a third flex item beside Copy and the flag. The
/// fix moves the action row inside a footer the control owns, so the panel can be the row's sibling.
/// That is a claim about tree shape, which markup can answer directly.
/// </remarks>
public class CoachReportEscapeAndLayoutTests
{
    private static async Task<(InteractiveTestRenderer Renderer, int Id, CoachWorkspaceState State)>
        MountAsync()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);

        var provider = services.BuildServiceProvider();
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var id = await renderer.RenderAsync<CoachReportControl>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(CoachReportControl.MessageId)] = "m-2" }));

        return (renderer, id, state);
    }

    // ------------------------------------------------------- the workspace knows a panel is open

    /// <summary>
    /// The overlay cannot stand down for something it does not know about. This is the signal that
    /// carries "a panel is open" out of the message and up to the host.
    /// </summary>
    [Fact]
    public async Task OpeningThePanelTellsTheWorkspaceAndClosingItTellsTheWorkspaceAgain()
    {
        var (renderer, id, state) = await MountAsync();

        state.IsReportPanelOpen.Should().BeFalse("nothing is open yet");

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        state.IsReportPanelOpen.Should().BeTrue();

        await renderer.ClickButtonByIdAsync(id, "coach-report-cancel-m-2");
        state.IsReportPanelOpen.Should().BeFalse("the flag is the only thing left on the row");
    }

    /// <summary>
    /// A control that goes away with its panel open must not leave the overlay deferring Escape to
    /// a surface that no longer exists.
    /// </summary>
    /// <remarks>
    /// This is the failure the registry's shape is chosen to prevent. Keyed on the message id it
    /// would survive the control — a transcript that pages, rebinds or unmounts would leave an entry
    /// nothing can ever remove, and the overlay would refuse to close for the rest of the session.
    /// </remarks>
    [Fact]
    public async Task AControlThatDisappearsWithItsPanelOpenReleasesTheOverlay()
    {
        var (renderer, id, state) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        state.IsReportPanelOpen.Should().BeTrue();

        await renderer.DisposeRootComponentAsync(id);

        state.IsReportPanelOpen.Should().BeFalse(
            "an unmounted control cannot be asked to close anything, so it must not still be claiming Escape");
    }

    /// <summary>
    /// The same control handed another message's identity is, for this purpose, a different control.
    /// </summary>
    [Fact]
    public async Task ARebindToAnotherMessageClosesThePanelAndReleasesTheOverlay()
    {
        var (renderer, id, state) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        state.IsReportPanelOpen.Should().BeTrue();

        await renderer.SetRootParametersAsync(id, ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(CoachReportControl.MessageId)] = "m-9" }));

        state.IsReportPanelOpen.Should().BeFalse(
            "the panel belonged to the message that was reassigned away");
        renderer.HasElementWithId(id, "coach-report-panel-m-9").Should().BeFalse(
            "a reused control must not carry somebody else's open form");
    }

    // ------------------------------------------------------- one Escape, one layer

    /// <summary>
    /// Escape pressed inside the message's own surface closes the panel and nothing else.
    /// </summary>
    [Fact]
    public async Task EscapeInsideTheFooterClosesOnlyThePanel()
    {
        var (renderer, id, state) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue();

        await renderer.PressKeyByIdAsync(id, "coach-message-footer-m-2", "Escape");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeFalse(
            "the press belonged to the panel");
        renderer.HasElementWithId(id, "coach-report-m-2").Should().BeTrue(
            "the flag is still there to open it again");
        state.IsReportPanelOpen.Should().BeFalse(
            "the overlay is free to answer the next press itself");
    }

    /// <summary>
    /// A press that is not Escape passes through. The row is not a keyboard trap.
    /// </summary>
    [Fact]
    public async Task AnotherKeyInsideTheFooterLeavesThePanelOpen()
    {
        var (renderer, id, state) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        await renderer.PressKeyByIdAsync(id, "coach-message-footer-m-2", "ArrowDown");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue(
            "arrow keys belong to the radio group, not to closing the form");
        state.IsReportPanelOpen.Should().BeTrue();
    }

    /// <summary>
    /// Escape pressed anywhere else reaches the overlay, which asks the panel to close rather than
    /// collapsing itself. One press, one layer, from either direction.
    /// </summary>
    [Fact]
    public async Task EscapeFromOutsideTheFooterIsHandedBackToThePanel()
    {
        var (renderer, id, state) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        SamOverlayEscape
            .Resolve(SamOverlayVisualState.Expanded, isConfirmingWrite: false, isReportPanelOpen: state.IsReportPanelOpen)
            .Should().Be(SamOverlayEscapeAction.DeferToPanelContent,
                "the overlay stands down while an inner surface owns the key");

        state.RequestCloseReportPanels().Should().BeTrue("there was something to close");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeFalse();
        state.IsReportPanelOpen.Should().BeFalse();

        SamOverlayEscape
            .Resolve(SamOverlayVisualState.Expanded, isConfirmingWrite: false, isReportPanelOpen: state.IsReportPanelOpen)
            .Should().Be(SamOverlayEscapeAction.Collapse,
                "the next press is the overlay's own");
    }

    /// <summary>
    /// Asking to close when nothing is open says so, so the host can fall through to its own rule
    /// instead of swallowing a press.
    /// </summary>
    [Fact]
    public async Task ClosingWhenNothingIsOpenReportsThatNothingHappened()
    {
        var (_, _, state) = await MountAsync();

        state.RequestCloseReportPanels().Should().BeFalse();
    }

    // ------------------------------------------------------- the panel is below the row

    /// <summary>
    /// The rejected shape: the panel as a flex sibling of Copy and the flag.
    /// </summary>
    [Fact]
    public async Task ThePanelRendersBelowTheActionRowAndNotInsideIt()
    {
        var (renderer, id, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        var ancestors = renderer.AncestorClassesOf(id, "coach-report-panel");

        ancestors.Should().Contain(c => c.Contains("coach-message-footer"),
            "the panel and the row share one owner");
        ancestors.Should().NotContain(c => c.Contains("coach-message-actions"),
            "a form inside a flex row of icon buttons is the defect this replaced");
    }

    /// <summary>
    /// Reading order matches visual order: the actions come first, the form they opened comes after.
    /// </summary>
    [Fact]
    public async Task TheActionRowIsRenderedBeforeThePanel()
    {
        var (renderer, id, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        var classes = renderer.RenderedClassesInOrder(id);
        var row = classes.ToList().FindIndex(c => c.Contains("coach-message-actions"));
        var panel = classes.ToList().FindIndex(c => c.Contains("coach-report-panel"));

        row.Should().BeGreaterThanOrEqualTo(0);
        panel.Should().BeGreaterThan(row,
            "a screen reader reads the form after the control that opened it, not before");
    }

    /// <summary>
    /// The flag stays where a learner reached for it. Moving it into the panel would mean the
    /// control that opens the form disappears the moment the form appears.
    /// </summary>
    [Fact]
    public async Task TheFlagStaysOnTheActionRowWhileThePanelIsOpen()
    {
        var (renderer, id, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        renderer.AncestorClassesOf(id, "coach-report-flag")
            .Should().Contain(c => c.Contains("coach-message-actions"),
                "the flag is an action; only the form moved");
    }

    /// <summary>
    /// The row exists for every response, whether or not it can be reported, because Copy lives on
    /// it too.
    /// </summary>
    [Fact]
    public async Task AMessageThatCannotBeReportedStillGetsItsActionRow()
    {
        var (renderer, id, _) = await MountAsync();

        await renderer.SetRootParametersAsync(id, ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(CoachReportControl.MessageId)] = null }));

        renderer.RenderedClassesInOrder(id).Should().Contain(c => c.Contains("coach-message-actions"));
        renderer.RenderedClassesInOrder(id).Should().NotContain(c => c.Contains("coach-report-flag"),
            "a notice is not a response a learner could be dissatisfied with");
    }

    // ------------------------------------------------------- more than one message on screen

    private static async Task<(InteractiveTestRenderer Renderer, int Id, CoachWorkspaceState State)>
        MountPaneAsync(int coachMessages = 2)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");

        for (var i = 0; i < coachMessages; i++)
        {
            client.Seed("c-1", CoachMessageRole.Learner, $"Question {i + 1}");
            client.Seed("c-1", CoachMessageRole.Coach, $"Answer {i + 1}");
        }

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);

        var provider = services.BuildServiceProvider();
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return (renderer, await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty), state);
    }

    /// <summary>
    /// Each message keeps its own panel. A conversation is a list of these controls, and a learner
    /// looking at two responses can have a form open on one of them without the other following.
    /// </summary>
    [Fact]
    public async Task OneMessagesPanelDoesNotOpenAnothers()
    {
        var (renderer, id, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue();
        renderer.HasElementWithId(id, "coach-report-panel-m-4").Should().BeFalse(
            "the other message was not asked about");
    }

    /// <summary>
    /// Two panels open at once is one state to the overlay, not two — and it stays that state until
    /// the last one closes, or Escape would collapse the overlay with a form still on screen.
    /// </summary>
    [Fact]
    public async Task TheOverlayStaysStoodDownUntilTheLastPanelCloses()
    {
        var (renderer, id, state) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-m-4");

        state.IsReportPanelOpen.Should().BeTrue();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        state.IsReportPanelOpen.Should().BeTrue("the second message's form is still open");
        renderer.HasElementWithId(id, "coach-report-panel-m-4").Should().BeTrue();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-4");

        state.IsReportPanelOpen.Should().BeFalse();
    }

    /// <summary>
    /// One press, one layer — so a press the overlay handed back closes every panel that was open,
    /// rather than leaving the learner to press Escape once per message.
    /// </summary>
    [Fact]
    public async Task TheDeferredPressClosesEveryOpenPanel()
    {
        var (renderer, id, state) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-m-4");

        (await renderer.Dispatcher.InvokeAsync(state.RequestCloseReportPanels)).Should().BeTrue();

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeFalse();
        renderer.HasElementWithId(id, "coach-report-panel-m-4").Should().BeFalse();
        state.IsReportPanelOpen.Should().BeFalse();
    }

    /// <summary>
    /// Closing the workspace with a panel open must not leave the overlay believing a panel is still
    /// there. The registry is keyed on the control, so a control torn down with its panel open
    /// releases the overlay — and the workspace's own reset clears anything that missed.
    /// </summary>
    [Fact]
    public async Task ClosingTheWorkspaceWithAPanelOpenReleasesTheOverlay()
    {
        var (renderer, id, state) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        state.IsReportPanelOpen.Should().BeTrue();

        await renderer.Dispatcher.InvokeAsync(state.Reset);

        state.IsReportPanelOpen.Should().BeFalse(
            "nothing on screen belongs to the conversation that had a panel open");
    }
}
