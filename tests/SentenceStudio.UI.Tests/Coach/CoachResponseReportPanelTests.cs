using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The report panel, driven by real presses on the production component.
/// </summary>
/// <remarks>
/// Rendered markup alone cannot answer the questions that matter here — does one press open the
/// panel, does Cancel leave nothing behind, does a failed report keep the panel open and say so —
/// because every one of them is a transition. These press the real handlers.
/// </remarks>
public class CoachResponseReportPanelTests
{
    private static async Task<(InteractiveTestRenderer Renderer, int Id, FakeCoachApiClient Client)>
        MountAsync(Action<FakeCoachApiClient>? configure = null)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");
        configure?.Invoke(client);

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

        return (renderer, id, client);
    }

    // ---------------------------------------------------------------- opening

    [Fact]
    public async Task OnePressOpensTheReasonPanel()
    {
        var (renderer, id, _) = await MountAsync();

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeFalse();
        renderer.AttributeValue(id, "coach-report-m-2", "aria-expanded").Should().Be("false");

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue();
        renderer.AttributeValue(id, "coach-report-m-2", "aria-expanded").Should().Be("true");
        renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePanelOffersEveryClosedReasonAndNoFreeText()
    {
        var (renderer, id, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        var text = renderer.RenderedText(id);

        text.Should().Contain("Did not answer my request");
        text.Should().Contain("Incorrect or misleading");
        text.Should().Contain("Expected an app action");
        text.Should().Contain("Confusing");
        text.Should().Contain("Other");

        foreach (var reason in Enum.GetValues<CoachResponseReportReason>())
        {
            renderer.HasElementWithId(id, $"coach-report-panel-m-2-{reason}").Should().BeTrue(
                $"{reason} must be reachable, not merely listed");
        }
    }

    [Fact]
    public async Task ThePanelOffersCancelAndReport()
    {
        var (renderer, id, _) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        renderer.HasElementWithId(id, "coach-report-cancel-m-2").Should().BeTrue();
        renderer.HasElementWithId(id, "coach-report-submit-m-2").Should().BeTrue();
    }

    // ---------------------------------------------------------------- cancelling

    [Fact]
    public async Task CancelClosesThePanelAndReportsNothing()
    {
        var (renderer, id, client) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-cancel-m-2");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeFalse();
        renderer.AttributeValue(id, "coach-report-m-2", "aria-expanded").Should().Be("false");
        renderer.RenderedText(id).Should().NotContain("Reported for review");
        client.Reports.Should().BeEmpty("cancelling is not a quiet report");
    }

    // ---------------------------------------------------------------- reporting

    [Fact]
    public async Task ReportingSettlesTheControlAndSendsTheChosenReason()
    {
        var (renderer, id, client) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.RenderedText(id).Should().Contain("Reported for review");
        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeFalse();

        client.Reports.Should().ContainSingle()
            .Which.Reason.Should().Be(CoachResponseReportReason.Confusing);
        renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task TheDefaultReasonIsSentWhenTheLearnerChangesNothing()
    {
        var (renderer, id, client) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        client.Reports.Should().ContainSingle()
            .Which.Reason.Should().Be(CoachResponseReportReason.DidNotAnswer,
                "the first choice is preselected so the control is never submitted with no reason at all");
    }

    [Fact]
    public async Task AReportedResponseOffersNoSecondPress()
    {
        var (renderer, id, client) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.HasElementWithId(id, "coach-report-m-2").Should().BeFalse(
            "there is nothing left to press once the response is reported");
        renderer.HasElementWithId(id, "coach-report-done-m-2").Should().BeTrue();
        client.Reports.Should().ContainSingle();
    }

    [Fact]
    public async Task AResponseTheServerAlreadyHasSettlesWithoutAnError()
    {
        var (renderer, id, _) = await MountAsync(client =>
            client.OnReportResponse = messageId => new CoachResponseReportResponse
            {
                MessageId = messageId,
                Reason = CoachResponseReportReason.Other,
                State = CoachResponseReportState.AlreadyReported,
                ReportedAtUtc = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc)
            });

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        var text = renderer.RenderedText(id);
        text.Should().Contain("Reported for review");
        text.Should().NotContain("could not be sent",
            "a repeat is a learner whose intent was already carried out, not a failure to explain away");
    }

    // ---------------------------------------------------------------- failure

    [Fact]
    public async Task AFailedReportKeepsThePanelOpenAndSaysSo()
    {
        var (renderer, id, _) = await MountAsync(client =>
            client.OnReportResponse = _ => throw FakeCoachApiClient.Gone());

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue(
            "closing here would look like the report was filed");

        var text = renderer.RenderedText(id);
        text.Should().Contain("That report could not be sent. Nothing was reported.");
        text.Should().NotContain("Reported for review");
    }

    // ---------------------------------------------------------------- rebinding

    /// <summary>
    /// A panel left open must never be submitted against a response the learner did not read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this pins is a positional rebind: Blazor diffs an unkeyed loop by index, so
    /// prepending an older page of history hands an existing control instance a different
    /// message and merely re-parameterizes it. The panel stays open, the chosen reason stays
    /// chosen, and submitting files a report against an exchange nobody complained about — one
    /// the server cannot refuse, because the substituted id is a real, owned, pairable response.
    /// </para>
    /// <para>
    /// Asserted on the control itself rather than through the transcript, because the control is
    /// where the guarantee has to live: the keyed loop is what should prevent the rebind, and this
    /// is what makes the rebind harmless if it ever happens anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARebindToAnotherResponseDiscardsTheOpenPanel()
    {
        var (renderer, id, client) = await MountAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");

        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue();

        // The same instance, now pointed at a different response.
        await renderer.SetRootParametersAsync(id, ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(CoachReportControl.MessageId)] = "m-9" }));

        renderer.HasElementWithId(id, "coach-report-panel-m-9").Should().BeFalse(
            "an open panel does not follow the learner's reason onto a response they never read");
        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeFalse();
        renderer.AttributeValue(id, "coach-report-m-9", "aria-expanded").Should().Be("false");

        client.Reports.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- localization

    [Fact]
    public async Task ThePanelIsLocalized()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ko");

        try
        {
            var (renderer, id, _) = await MountAsync();

            await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
            var text = renderer.RenderedText(id);

            text.Should().Contain("요청한 내용에 답하지 않음");
            text.Should().Contain("답변 신고");
            text.Should().NotContain("Did not answer my request");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }
}
