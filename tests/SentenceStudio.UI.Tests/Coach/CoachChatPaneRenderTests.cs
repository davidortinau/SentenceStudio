using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Renders the conversation to real HTML: one answer not two, log semantics, and the
/// plan affordances withdrawing when there is no plan to edit.
/// </summary>
public class CoachChatPaneRenderTests
{
    private static async Task<string> RenderAsync(CoachWorkspaceState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
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

    private static CoachTurnResponse AnswerTurn(CoachAnswerDto answer, PendingCoachSuggestionDto? pending = null) =>
        CoachStateMachineTests.Turn(
            sessionStatus: pending is null ? CoachSessionStatus.Active : CoachSessionStatus.SuggestionPending,
            suggestion: pending,
            messages: [CoachAnswerStateTests.AnswerMessage(answer)],
            answer: answer);

    private static async Task<CoachWorkspaceState> AfterAnswerAsync(
        Action<FakeCoachApiClient>? configure = null,
        PendingCoachSuggestionDto? pending = null)
    {
        var client = new FakeCoachApiClient();
        configure?.Invoke(client);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        client.OnSubmitTurn = _ => AnswerTurn(answer, pending);
        state.Draft = "What is the difference between 은/는 and 이/가?";
        await state.SendDraftAsync();

        return state;
    }

    // ---------------------------------------------------------------- no duplication

    [Fact]
    public async Task AnAnswerRendersOnceAsBlocksNotAlsoAsPlainText()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        // The server sends the same text twice — structured and as the message body. Exactly one
        // copy may reach the learner.
        Regex.Matches(html, Regex.Escape("은/는 marks the topic; 이/가 marks the subject.")).Count
            .Should().Be(1, "the plain text must not render beside the blocks");

        html.Should().Contain("coach-answer", "the structured blocks are what rendered");
    }

    [Fact]
    public async Task TheKoreanExampleIsLanguageTaggedInsideTheConversation()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().Contain("lang=\"ko\"");
        html.Should().Contain("제가 했어요");
    }

    // ---------------------------------------------------------------- log semantics

    [Fact]
    public async Task TheConversationIsANamedLogRegion()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().Contain("role=\"log\"", "appended turns are announced in order");
        html.Should().Contain("aria-label=\"Conversation with Sam\"");
    }

    // ---------------------------------------------------------------- plan affordances

    [Fact]
    public async Task QuickConstraintsAppearAsStartersInAnEmptyConversation()
    {
        // Before the learner has said anything the chips are prompts: something to start from.
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var html = await RenderAsync(state);

        html.Should().Contain("coach-chip-row", "an empty conversation needs somewhere to start");
    }

    [Fact]
    public async Task QuickConstraintsLeaveTheConversationOnceItHasStarted()
    {
        // Once there is a conversation they are no longer prompts, they are plan controls, and
        // plan controls live in the canvas.
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().NotContain("coach-chip-row",
            "after the first turn the chips belong to the plan pane, not the chat");
    }

    [Fact]
    public async Task QuickConstraintsAreWithdrawnWhenThereIsNoPlanToEdit()
    {
        var state = await AfterAnswerAsync(client => client.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = false
        });

        var html = await RenderAsync(state);

        html.Should().NotContain("coach-chip-row", "there is no plan for these to change");
        html.Should().Contain("You can still ask language questions.",
            "and the learner is told plainly that the conversation still works");
    }

    [Fact]
    public async Task TheNoPlanExplanationIsNeutralNotAnError()
    {
        var state = await AfterAnswerAsync(client => client.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = false
        });

        var html = await RenderAsync(state);

        html.Should().NotContain("role=\"alert\"", "no plan is a normal state");
        html.Should().NotContain("coach-card-alert");
    }

    // ---------------------------------------------------------------- mixed turn

    [Fact]
    public async Task AMixedTurnShowsTheAnswerThenExactlyOneActionPair()
    {
        var state = await AfterAnswerAsync(pending: CoachStateMachineTests.Suggestion("sug-mixed"));
        var html = await RenderAsync(state);

        var answerAt = html.IndexOf("은/는 marks the topic", StringComparison.Ordinal);
        var cardAt = html.IndexOf("coach-card-suggestion", StringComparison.Ordinal);

        answerAt.Should().BeGreaterThan(-1);
        cardAt.Should().BeGreaterThan(answerAt, "the answer is read before the offer");

        // Counting every button in the pane would also catch the quick-constraint chips, so
        // assert on the decision pair itself: exactly one accept and exactly one decline.
        Regex.Matches(html, Regex.Escape("Include speaking")).Count.Should().Be(1, "exactly one Accept");
        Regex.Matches(html, Regex.Escape("Not now")).Count.Should().Be(1, "exactly one Not now");
        Regex.Matches(html, Regex.Escape("coach-card-suggestion")).Count.Should().Be(1, "one offer, one card");
    }

    [Fact]
    public async Task APureAnswerShowsNoSuggestionCardAndNoReceipt()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().NotContain("coach-card-suggestion");
        html.Should().NotContain("coach-card-receipt", "a language question changes no plan");
    }

    // ---------------------------------------------------------------- resume

    [Fact]
    public async Task AResumedSessionWithNoTranscriptExplainsItselfWithoutInventingTurns()
    {
        var client = new FakeCoachApiClient();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay, "session-7");

        var html = await RenderAsync(state);

        html.Should().Contain("Earlier messages are not shown after a reload.");
        html.Should().NotContain("coach-answer", "no answers are reconstructed");
    }
}
