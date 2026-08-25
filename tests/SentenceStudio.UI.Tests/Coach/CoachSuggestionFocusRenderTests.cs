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
/// A pending suggestion that would change the vocabulary focus.
/// </summary>
/// <remarks>
/// <c>VocabularyFocus</c> is the authoritative shape and carries the frozen selection, so what the
/// card shows is exactly what acceptance applies. <c>Rationale</c> is a deterministic English
/// fallback built from the same numbers; a localizing client uses the projection and suppresses
/// the fallback rather than printing both.
/// </remarks>
public class CoachSuggestionFocusRenderTests
{
    private const string EnglishRationale = "Focus on 3 action verbs from your vocabulary.";

    private static PendingCoachSuggestionDto FocusSuggestion(CoachVocabularyFocusDto? focus)
    {
        var baseline = CoachStateMachineTests.Suggestion();

        return new PendingCoachSuggestionDto
        {
            SuggestionId = baseline.SuggestionId,
            Delta = baseline.Delta,
            Rationale = EnglishRationale,
            Preview = baseline.Preview,
            Evidence = baseline.Evidence,
            AcceptLabel = baseline.AcceptLabel,
            RejectLabel = baseline.RejectLabel,
            CreatedAtUtc = baseline.CreatedAtUtc,
            ExpiresAtUtc = baseline.ExpiresAtUtc,
            VocabularyFocus = focus
        };
    }

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
            var output = await renderer.RenderComponentAsync<CoachSuggestionCard>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static async Task<CoachWorkspaceState> PendingAsync(
        CoachVocabularyFocusDto? focus,
        CoachAnswerDto? answer = null)
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var suggestion = FocusSuggestion(focus);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: suggestion,
            messages: answer is null ? null : [CoachAnswerStateTests.AnswerMessage(answer)],
            answer: answer);

        state.Draft = "focus today on action verbs";
        await state.SendDraftAsync();
        return state;
    }

    /// <summary>A resumed session that re-reads the same frozen suggestion.</summary>
    private static async Task<CoachWorkspaceState> ResumedAsync(CoachVocabularyFocusDto focus)
    {
        var client = new FakeCoachApiClient();
        client.OnGetSession = id => FakeCoachApiClient.Session(
            id,
            status: CoachSessionStatus.SuggestionPending,
            suggestion: FocusSuggestion(focus));

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");
        return state;
    }

    // ---------------------------------------------------------------- the proposed set

    [Fact]
    public async Task TheProposedSetIsRenderedInOrder()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        var first = html.IndexOf("달리다", StringComparison.Ordinal);
        var second = html.IndexOf("가다", StringComparison.Ordinal);
        var third = html.IndexOf("먹다", StringComparison.Ordinal);

        first.Should().BeGreaterThan(-1);
        first.Should().BeLessThan(second);
        second.Should().BeLessThan(third, "acceptance applies this exact order");
    }

    [Fact]
    public async Task TheProposedTermsCarryTheirLanguageAndGloss()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        Regex.Matches(html, "lang=\"ko\"").Count.Should().Be(3);
        html.Should().Contain("to run").And.Contain("lang=\"en\"");
    }

    [Fact]
    public async Task TheProposedCountIsShown()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        html.Should().Contain("3 of 12 matching words");
    }

    [Fact]
    public async Task AReloadShowsTheSameFrozenSet()
    {
        var html = await RenderAsync(await ResumedAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        html.Should().Contain("달리다").And.Contain("가다").And.Contain("먹다");
        html.Should().Contain("3 of 12 matching words",
            "a reload re-reads the frozen selection, down to the count");
    }

    // ---------------------------------------------------------------- rationale suppression

    [Fact]
    public async Task TheEnglishRationaleIsSuppressedWhenAProjectionIsPresent()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        html.Should().NotContain(EnglishRationale,
            "the projection is authoritative and localizable; printing both says it twice");
    }

    [Fact]
    public async Task TheLocalizedSummaryReplacesIt()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        html.Should().Contain("Sam suggests focusing on action verbs.");
    }

    [Fact]
    public async Task ANonFocusSuggestionKeepsItsRationale()
    {
        var html = await RenderAsync(await PendingAsync(focus: null));

        html.Should().Contain(EnglishRationale,
            "with no projection to localize, the server sentence is all there is");
        html.Should().NotContain("coach-focus-words");
    }

    // ---------------------------------------------------------------- one decision

    [Fact]
    public async Task ThereIsStillExactlyOneActionPair()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        Regex.Matches(html, "<button").Count.Should().Be(2,
            "a focus proposal is still one decision, not two");
        Regex.Matches(html, Regex.Escape("Not now")).Count.Should().Be(1);
    }

    [Fact]
    public async Task TheSetIsReadBeforeTheActionsThatDecideIt()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        var summary = html.IndexOf("Sam suggests focusing on", StringComparison.Ordinal);
        var words = html.IndexOf("coach-focus-words", StringComparison.Ordinal);
        var actions = html.IndexOf("<button", StringComparison.Ordinal);

        summary.Should().BeGreaterThan(-1);
        summary.Should().BeLessThan(words, "the summary introduces the list");
        words.Should().BeLessThan(actions, "the learner reads what they are accepting first");
    }

    // ---------------------------------------------------------------- mixed turn

    [Fact]
    public async Task AMixedTurnAnswersFirstThenProposesTheFocus()
    {
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        var state = await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs(), answer);

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

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });

        var question = html.IndexOf("focus today on action verbs", StringComparison.Ordinal);
        var answerText = html.IndexOf("은/는 marks the topic", StringComparison.Ordinal);
        var proposal = html.IndexOf("Sam suggests focusing on", StringComparison.Ordinal);

        question.Should().BeGreaterThan(-1);
        answerText.Should().BeGreaterThan(question, "the answer follows the question");
        proposal.Should().BeGreaterThan(answerText, "the proposal follows the answer");

        Regex.Matches(html, Regex.Escape("Not now")).Count.Should().Be(1);
    }

    // ---------------------------------------------------------------- nothing leaks

    [Fact]
    public async Task NoInternalIdentifierReachesTheCard()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        html.Should().NotContain("grammar.action-verb");
    }

    [Fact]
    public async Task NoReviewOrMasteryLanguageAppears()
    {
        var html = await RenderAsync(await PendingAsync(CoachVocabularyFocusRenderTests.ActionVerbs()));

        foreach (var banned in new[] { "due", "mastery", "overdue" })
        {
            html.Should().NotContainEquivalentOf(banned);
        }
    }
}
