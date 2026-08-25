using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Where focus lands when the report panel settles or closes, and what the transcript's autoscroll
/// is told while a disclosure changes the conversation's height.
/// </summary>
/// <remarks>
/// <para>
/// Both were rejected on the previous revision for the same reason: the component said the right
/// thing and nothing checked that it reached the browser. A focus call is a JS interop call, so the
/// only honest test records interop. These mount the <b>whole pane</b> rather than the control on
/// its own, so <c>OnReturnFocus</c>, <c>OnDisclosureChanging</c> and <c>OnDisclosureChanged</c> are
/// wired the way production wires them; a test that passed its own callbacks would be testing the
/// test.
/// </para>
/// <para>
/// <see cref="ModuleAwareJSRuntime"/> is used rather than the plain stub because
/// <c>focusElement</c> is a module-only export of app.js: invoked on the default runtime it throws
/// and takes the circuit with it. Recording module calls separately from global ones is what makes
/// that distinction visible.
/// </para>
/// </remarks>
public class CoachReportFocusAndScrollTests
{
    private static async Task<(InteractiveTestRenderer Renderer, int Id, ModuleAwareJSRuntime Js, CoachWorkspaceState State)>
        MountPaneAsync(Action<FakeCoachApiClient>? configure = null)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");
        configure?.Invoke(client);

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        return await HostAsync(state);
    }

    /// <summary>
    /// A conversation whose newest turn cited evidence, so the evidence disclosure is offered.
    /// </summary>
    /// <remarks>
    /// Evidence belongs to a turn, not to a stored row — a durable conversation read back from the
    /// server carries none, because the server keeps no plaintext transcript to attach it to. So the
    /// evidence disclosure can only be reached by taking a turn.
    /// </remarks>
    private static async Task<(InteractiveTestRenderer Renderer, int Id, ModuleAwareJSRuntime Js, CoachWorkspaceState State)>
        MountPaneAfterTurnAsync(IReadOnlyList<CoachEvidenceDto> evidence)
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages:
            [
                new CoachMessageDto
                {
                    MessageId = "m-2",
                    Role = CoachMessageRole.Coach,
                    Kind = CoachMessageKind.Text,
                    Text = "You have been reading more than speaking.",
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
                }
            ],
            evidence: evidence);

        state.Draft = "How am I doing?";
        await state.SendDraftAsync();

        return await HostAsync(state);
    }

    private static async Task<(InteractiveTestRenderer Renderer, int Id, ModuleAwareJSRuntime Js, CoachWorkspaceState State)>
        HostAsync(CoachWorkspaceState state)
    {
        var js = new ModuleAwareJSRuntime();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => js);
        services.AddScoped(_ => state);

        var provider = services.BuildServiceProvider();
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var id = await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty);

        return (renderer, id, js, state);
    }

    // ------------------------------------------------------- the target can hold focus

    /// <summary>
    /// The settled state is a span, and a span is not focusable. Without an explicit tabindex the
    /// focus call is a silent no-op and the browser drops focus to the document — which on a long
    /// transcript loses the learner's place entirely.
    /// </summary>
    [Fact]
    public async Task TheSettledStateIsProgrammaticallyFocusable()
    {
        var (renderer, id, _, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.AttributeValue(id, "coach-report-done-m-2", "tabindex").Should().Be("-1",
            "negative, so script can reach it and Tab still skips it");
    }

    // ------------------------------------------------------- focus actually moves

    /// <summary>
    /// The control the learner pressed is gone after a successful report. Something has to take its
    /// place, and it has to be told to.
    /// </summary>
    [Fact]
    public async Task SubmittingMovesFocusToTheSettledState()
    {
        var (renderer, id, js, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        js.ModuleInvocations.Should().Contain("focusElement",
            "a report that settles without moving focus leaves the learner nowhere");
        js.FirstArgOf("focusElement").Should().Be("coach-report-done-m-2",
            "focus goes to the thing that replaced the control, not to the top of the page");
    }

    /// <summary>
    /// Cancel puts the learner back where they started, which is the control they pressed.
    /// </summary>
    [Fact]
    public async Task CancellingReturnsFocusToTheFlag()
    {
        var (renderer, id, js, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-cancel-m-2");

        js.FirstArgOf("focusElement").Should().Be("coach-report-m-2");
    }

    /// <summary>
    /// Escape is Cancel by another name, and must land in the same place.
    /// </summary>
    [Fact]
    public async Task EscapeReturnsFocusToTheFlag()
    {
        var (renderer, id, js, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.PressKeyByIdAsync(id, "coach-message-footer-m-2", "Escape");

        js.FirstArgOf("focusElement").Should().Be("coach-report-m-2",
            "closing with the keyboard must not scatter focus differently from closing with the mouse");
    }

    /// <summary>
    /// The overlay closing the panel on the learner's behalf is still the learner closing it.
    /// </summary>
    [Fact]
    public async Task ClosingFromTheOverlayAlsoReturnsFocusToTheFlag()
    {
        var (renderer, id, js, state) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.Dispatcher.InvokeAsync(() => state.RequestCloseReportPanels());

        js.FirstArgOf("focusElement").Should().Be("coach-report-m-2");
    }

    /// <summary>
    /// A report that failed has settled nothing. The panel stays, and so does focus.
    /// </summary>
    [Fact]
    public async Task AFailedReportDoesNotMoveFocusAnywhere()
    {
        var (renderer, id, js, _) = await MountPaneAsync(
            client => client.OnReportResponse = _ => throw new InvalidOperationException("server said no"));

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue(
            "the report did not happen, so the form the learner filled in is still theirs");
        js.ModuleInvocations.Should().NotContain("focusElement",
            "moving focus would be telling the learner something finished when it did not");
    }

    // ------------------------------------------------------- the scroll bracket

    /// <summary>
    /// Opening the panel is a resize the transcript is warned about and told when to re-measure.
    /// </summary>
    [Fact]
    public async Task OpeningTheReportPanelBracketsTheResize()
    {
        var (renderer, id, js, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        var calls = js.ModuleInvocations;
        calls.Should().Contain("beginCoachViewportChange");
        calls.Should().Contain("endCoachViewportChange");
        calls.IndexOf("beginCoachViewportChange").Should().BeLessThan(
            calls.IndexOf("endCoachViewportChange"),
            "suspending after the growth has already been measured suspends nothing");
    }

    /// <summary>
    /// Closing it is the same event in reverse and gets the same treatment. Only bracketing the
    /// opening would leave the shrink to be read as the transcript losing content.
    /// </summary>
    [Fact]
    public async Task ClosingTheReportPanelBracketsTheResizeAsWell()
    {
        var (renderer, id, js, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        var opened = js.ModuleInvocations.Count(c => c == "beginCoachViewportChange");

        await renderer.ClickButtonByIdAsync(id, "coach-report-cancel-m-2");

        js.ModuleInvocations.Count(c => c == "beginCoachViewportChange")
            .Should().BeGreaterThan(opened);
        js.ModuleInvocations.Count(c => c == "beginCoachViewportChange")
            .Should().Be(js.ModuleInvocations.Count(c => c == "endCoachViewportChange"),
                "an unclosed bracket leaves the transcript's content rules suspended for good");
    }

    /// <summary>
    /// Evidence is the other disclosure and resizes the same transcript in the same way.
    /// </summary>
    [Fact]
    public async Task TheEvidenceDisclosureBracketsItsResizeToo()
    {
        var (renderer, id, js, _) = await MountPaneAfterTurnAsync([PracticeBalance()]);

        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");

        js.ModuleInvocations.Should().Contain("beginCoachViewportChange");
        js.ModuleInvocations.Count(c => c == "beginCoachViewportChange")
            .Should().Be(js.ModuleInvocations.Count(c => c == "endCoachViewportChange"));

        var opened = js.ModuleInvocations.Count(c => c == "beginCoachViewportChange");

        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");

        js.ModuleInvocations.Count(c => c == "beginCoachViewportChange")
            .Should().BeGreaterThan(opened, "collapsing is a resize too");
        js.ModuleInvocations.Count(c => c == "beginCoachViewportChange")
            .Should().Be(js.ModuleInvocations.Count(c => c == "endCoachViewportChange"));
    }

    /// <summary>
    /// The bracket is closed after focus has moved, so the scroll correction has the last word about
    /// where the transcript rests.
    /// </summary>
    /// <remarks>
    /// The other order looks harmless and is not: focusing a control the browser considers offscreen
    /// scrolls it into view, so a focus call landing after the re-baseline moves the transcript to a
    /// position the re-baseline has already recorded as settled.
    /// </remarks>
    [Fact]
    public async Task FocusMovesBeforeTheBracketCloses()
    {
        var (renderer, id, js, _) = await MountPaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        var focus = js.ModuleInvocations.LastIndexOf("focusElement");
        var end = js.ModuleInvocations.LastIndexOf("endCoachViewportChange");

        focus.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(focus,
            "the scroll correction is what decides where the transcript rests, so it goes last");
    }

    private static CoachEvidenceDto PracticeBalance() => new()
    {
        Kind = CoachEvidenceKind.PracticeBalance,
        Label = "Practice balance",
        Summary = "Mostly reading this week.",
        WindowStartDate = new DateOnly(2026, 8, 14),
        WindowEndDate = new DateOnly(2026, 8, 20),
        Values =
        [
            new CoachEvidenceValueDto { Label = "Input minutes", Value = 42, Unit = CoachEvidenceUnit.Minutes }
        ]
    };
}
