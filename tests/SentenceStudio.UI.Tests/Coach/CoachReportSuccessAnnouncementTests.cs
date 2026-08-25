using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// A filed report is announced exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Canvas found the outcome read twice. The settled state says "Reported for review" visibly and
/// takes focus, and the polite region underneath was being handed the same words — so a screen
/// reader announced the live region and then announced the focus move, and anything reading the
/// page whole found the string in the accessibility tree twice.
/// </para>
/// <para>
/// The fix is to let one node own the outcome. The polite region carries the in-flight state and
/// nothing else, and it empties when the work finishes. These tests count occurrences rather than
/// asserting presence, because presence was already true when the bug shipped.
/// </para>
/// </remarks>
public class CoachReportSuccessAnnouncementTests
{
    // ---------------------------------------------------------------- fixtures

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> ReportableAsync()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        return (state, client);
    }

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider Services(
        CoachWorkspaceState state,
        IJSRuntime js)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped(_ => js);
        services.AddScoped(_ => state);

        return services.BuildServiceProvider();
    }

    private static async Task<(InteractiveTestRenderer Renderer, int Id, ModuleAwareJSRuntime Js, FakeCoachApiClient Client, CoachWorkspaceState State)> MountAsync()
    {
        var js = new ModuleAwareJSRuntime();
        var (state, client) = await ReportableAsync();
        var provider = Services(state, js);
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var id = await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty);

        return (renderer, id, js, client, state);
    }

    /// <summary>Counts non-overlapping occurrences of the completion wording in rendered markup.</summary>
    private static int Occurrences(string markup, string phrase) =>
        Regex.Matches(markup, Regex.Escape(phrase)).Count;

    private const string Done = "Reported for review";

    // ---------------------------------------------------------------- the count

    /// <summary>
    /// The bug, stated as a number. Before the fix this rendered twice: once in the visible settled
    /// state and once in the visually hidden polite region.
    /// </summary>
    [Fact]
    public async Task TheOutcomeAppearsExactlyOnceAfterASuccessfulReport()
    {
        var (renderer, id, _, client, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        client.Reports.Should().ContainSingle("the report itself is unaffected by how it is announced");

        Occurrences(renderer.RenderedText(id), Done).Should().Be(1,
            "the settled state says it and takes focus; a second node saying the same words is the "
            + "same outcome announced twice");
    }

    /// <summary>
    /// Which node kept the wording matters: the visible focusable one, not the hidden region.
    /// </summary>
    [Fact]
    public async Task TheVisibleSettledStateIsTheNodeThatCarriesTheOutcome()
    {
        var (renderer, id, _, _, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.HasElementWithId(id, "coach-report-done-m-2").Should().BeTrue();
        renderer.AttributeValue(id, "coach-report-done-m-2", "tabindex").Should().Be("-1",
            "script can reach it and Tab still skips it");

        renderer.AttributesOfElementWithId(id, "coach-report-done-m-2")
            .Should().NotContain("role=\"status\"",
                "a live region and a focus move on the same node announces one outcome twice");
    }

    /// <summary>
    /// Focus is the announcement, so it has to actually happen — asserted through the interop
    /// recorder rather than inferred from the element existing.
    /// </summary>
    [Fact]
    public async Task FocusMovesToTheSettledStateSoTheOutcomeIsHeardOnce()
    {
        var (renderer, id, js, _, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        js.FirstArgOf("focusElement").Should().Be("coach-report-done-m-2",
            "the flag that had focus is gone, and the focus move is what reads the outcome out");
    }

    // ---------------------------------------------------------------- the region

    /// <summary>
    /// The polite region survives the report — it has to, or the next in-flight state would be
    /// announced into a region the screen reader has not been watching — but it says nothing.
    /// </summary>
    [Fact]
    public async Task ThePoliteRegionIsStillPresentAndEmptyOnceTheReportLands()
    {
        var (renderer, id, _, _, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.HasElementWithId(id, "coach-report-status-m-2").Should().BeTrue(
            "an empty region is the only kind a screen reader reliably announces into later");

        renderer.AttributeValue(id, "coach-report-status-m-2", "role").Should().Be("status");
        renderer.AttributeValue(id, "coach-report-status-m-2", "aria-live").Should().Be("polite");
    }

    /// <summary>
    /// The in-flight wording is what the region is for, and it is gone by the time the outcome is
    /// on screen. Held open with the gate so the busy state can be read while it is real.
    /// </summary>
    [Fact]
    public async Task TheSubmittingStatusIsAnnouncedAndThenRemovedOnCompletion()
    {
        var gate = new TaskCompletionSource();

        var (renderer, id, _, client, _) = await MountAsync();
        client.ReportGate = gate;

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");

        var submit = renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        var inFlight = renderer.RenderedText(id);
        inFlight.Should().Contain("Sending report", "the disabled controls do not say anything out loud");
        Occurrences(inFlight, Done).Should().Be(0, "nothing has been reported yet");
        renderer.AttributeValue(id, "coach-report-panel-m-2", "aria-busy").Should().Be("true");

        gate.SetResult();
        await submit;

        var settled = renderer.RenderedText(id);
        settled.Should().NotContain("Sending report", "the work finished, so the busy wording is stale");
        Occurrences(settled, Done).Should().Be(1);
    }

    /// <summary>
    /// The failure path already worked this way — one visible node, role="alert", no echo — and the
    /// success fix must not have made it the odd one out.
    /// </summary>
    [Fact]
    public async Task AFailedReportSaysSoOnceAndDoesNotClaimSuccess()
    {
        var (renderer, id, _, client, _) = await MountAsync();
        client.OnReportResponse = _ => throw FakeCoachApiClient.Gone();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        var text = renderer.RenderedText(id);
        Occurrences(text, "That report could not be sent").Should().Be(1);
        Occurrences(text, Done).Should().Be(0, "nothing was reported");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue(
            "closing here would look like the report was filed");
    }

    // ---------------------------------------------------------------- more than one message

    /// <summary>
    /// Two reported responses read as two outcomes, not four. The count is per message, so a
    /// regression that re-added the echo would show up here as double.
    /// </summary>
    [Fact]
    public async Task TwoReportedResponsesReadAsTwoOutcomes()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");
        client.Seed("c-1", CoachMessageRole.Learner, "And 이/가?");
        client.Seed("c-1", CoachMessageRole.Coach, "이/가 marks the subject.");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var provider = Services(state, new ModuleAwareJSRuntime());
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var id = await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty);

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        Occurrences(renderer.RenderedText(id), Done).Should().Be(1,
            "reporting one response says so once, and the other response has not been reported");

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-4");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-4");

        Occurrences(renderer.RenderedText(id), Done).Should().Be(2,
            "two settled states, one each — not one per state plus one per hidden echo");

        renderer.HasElementWithId(id, "coach-report-done-m-2").Should().BeTrue();
        renderer.HasElementWithId(id, "coach-report-done-m-4").Should().BeTrue();
    }
}
