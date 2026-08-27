using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The inline report control, rendered to real HTML and driven through the real workspace state.
/// </summary>
/// <remarks>
/// <para>
/// These cover the two failure modes a render test can see and a unit test cannot: a control
/// offered on the wrong artifact, and a control that settles into "Reported for review" without
/// the server having said so. The second one matters more than it looks — an optimistic reported
/// state is a claim that feedback reached a person, and unlike an optimistic message there is no
/// later correction the learner would ever see.
/// </para>
/// <para>
/// Rendered with <see cref="HtmlRenderer"/> rather than asserted on state, because the accessible
/// name, the 44px target class, and the <c>aria-expanded</c>/<c>aria-controls</c> association are
/// facts about the markup.
/// </para>
/// </remarks>
public class CoachResponseReportRenderTests
{
    private static async Task<string> RenderAsync(CoachWorkspaceState state, string culture = "en")
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
            services.AddScoped(_ => state);

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// A durable conversation with one learner question and one of Sam's answers on screen.
    /// </summary>
    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> ConversationAsync(
        Action<FakeCoachApiClient>? configure = null)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");
        configure?.Invoke(client);

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        return (state, client);
    }

    // ---------------------------------------------------------------- the control exists

    [Fact]
    public async Task ARealResponseCarriesAFlagBesideCopy()
    {
        var (state, _) = await ConversationAsync();

        var html = await RenderAsync(state);

        html.Should().Contain("coach-report-flag", "every one of Sam's responses can be reported");
        html.Should().Contain("aria-label=\"Report this response\"");
        html.Should().Contain("bi bi-flag", "the icon comes from the Bootstrap set, never an emoji");
        html.Should().Contain("coach-copy", "report sits beside copy, it does not replace it");
    }

    [Fact]
    public async Task TheFlagCarriesTheDisclosureAssociationBeforeItIsOpened()
    {
        var (state, _) = await ConversationAsync();

        var html = await RenderAsync(state);

        html.Should().Contain("aria-expanded=\"false\"");
        html.Should().Contain("aria-controls=\"coach-report-panel-m-2\"",
            "the association names a stable id derived from the server's own message id");
        html.Should().Contain("id=\"coach-report-m-2\"");
    }

    [Fact]
    public async Task TheLearnersOwnMessageIsNotReportable()
    {
        var (state, _) = await ConversationAsync();

        var html = await RenderAsync(state);

        // The learner's message is m-1 and Sam's is m-2. Only Sam's may carry a control.
        html.Should().NotContain("coach-report-m-1",
            "a learner cannot report their own words back to us");
        html.Should().Contain("coach-report-m-2");
    }

    [Fact]
    public async Task NoFlagIsOfferedWhenTheServerDoesNotAcceptReports()
    {
        var (state, _) = await ConversationAsync(client => client.IsReportingAvailable = false);

        var html = await RenderAsync(state);

        html.Should().NotContain("coach-report-flag",
            "a control that will 404 is worse than no control: the learner would read the 404 as the app being broken");
        html.Should().Contain("coach-copy", "the rest of the message actions are unaffected");
    }

    // ---------------------------------------------------------------- the settled state

    [Fact]
    public async Task AReportedResponseShowsTheSettledStateAndOffersNoSecondPress()
    {
        var (state, _) = await ConversationAsync(client => client.ReportedResponses.Add("m-2"));

        var html = await RenderAsync(state);

        html.Should().Contain("coach-report-done");
        html.Should().Contain("Reported for review");
        html.Should().NotContain("coach-report-flag",
            "there is nothing left to press, and a disabled control that looks pressable gets pressed");
    }

    [Fact]
    public async Task TheSettledStateSurvivesAReload()
    {
        // First circuit: the learner reports the response.
        var (state, client) = await ConversationAsync();
        var outcome = await state.ReportResponseAsync("m-2", CoachResponseReportReason.Confusing);
        outcome.Should().Be(CoachReportOutcome.Recorded);

        // Second circuit over the same server: everything the browser knew is gone.
        var directory = new CoachConversationDirectory(client);
        var reloaded = new CoachWorkspaceState(client, directory);
        await reloaded.OpenAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderAsync(reloaded);

        html.Should().Contain("Reported for review",
            "the reported state is the server's, so a browser that forgot everything is still told the truth");
    }

    // ---------------------------------------------------------------- localization

    [Fact]
    public async Task TheControlIsLocalizedAndTheCoachKeepsItsKoreanName()
    {
        var (state, _) = await ConversationAsync();

        var html = await RenderAsync(state, culture: "ko");

        html.Should().Contain("이 답변 신고", "the accessible name is localized, not left in English");
        html.Should().NotContain("Report this response");
    }

    [Fact]
    public async Task TheKoreanSettledStateIsLocalizedToo()
    {
        var (state, _) = await ConversationAsync(client => client.ReportedResponses.Add("m-2"));

        var html = await RenderAsync(state, culture: "ko");

        html.Should().Contain("검토 요청됨");
        html.Should().NotContain("Reported for review");
    }
}
