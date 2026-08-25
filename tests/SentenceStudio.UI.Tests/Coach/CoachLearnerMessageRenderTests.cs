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
/// The learner's turn as it actually reaches the browser: present, in order, labelled, styled
/// as theirs, and escaped. Rendered through the real renderer, so these fail if the markup
/// stops emitting them rather than if a property changes name.
/// </summary>
public class CoachLearnerMessageRenderTests
{
    private static async Task<string> RenderAsync(CoachWorkspaceState state, bool decode = true)
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
            var html = output.ToHtmlString();
            return decode ? System.Net.WebUtility.HtmlDecode(html) : html;
        });
    }

    private static CoachMessageDto Reply(string id, string text) => new()
    {
        MessageId = id,
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Text,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static async Task<CoachWorkspaceState> AfterAskingAsync(
        string question,
        CoachTurnResponse? response = null)
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => response ?? CoachStateMachineTests.Turn(
            messages: [Reply("m-1", "Use the topic marker.")]);

        state.Draft = question;
        await state.SendDraftAsync();
        return state;
    }

    // ---------------------------------------------------------------- presence and order

    [Fact]
    public async Task TheLearnerQuestionIsRendered()
    {
        var html = await RenderAsync(await AfterAskingAsync("How do I use the topic marker?"));

        html.Should().Contain("How do I use the topic marker?",
            "the learner must see what they asked");
    }

    [Fact]
    public async Task TheQuestionRendersBeforeTheReply()
    {
        var html = await RenderAsync(await AfterAskingAsync("How do I use the topic marker?"));

        var question = html.IndexOf("How do I use the topic marker?", StringComparison.Ordinal);
        var reply = html.IndexOf("Use the topic marker.", StringComparison.Ordinal);

        question.Should().BeGreaterThan(-1);
        reply.Should().BeGreaterThan(question, "role=log reads in document order: question then answer");
    }

    [Fact]
    public async Task TheQuestionIsLabelledAsTheLearners()
    {
        var html = await RenderAsync(await AfterAskingAsync("Mine"));

        html.Should().Contain("coach-message-learner", "learner turns carry their own styling hook");
        html.Should().Contain(">You<", "the speaker label is localized, not a colour cue");
    }

    [Fact]
    public async Task TheReplyIsAttributedToSamNotToARoleNoun()
    {
        var html = await RenderAsync(await AfterAskingAsync("Who are you?"));

        html.Should().Contain(">Sam<", "the coach is a person with a name");
        Regex.Matches(html, ">Coach<").Count.Should().Be(0,
            "the speaker label must not fall back to the role noun");
    }

    // ---------------------------------------------------------------- escaping

    [Fact]
    public async Task LearnerMarkupIsEscapedNotInterpreted()
    {
        var state = await AfterAskingAsync("<script>alert('x')</script> and <b>bold</b>");
        var html = await RenderAsync(state, decode: false);

        html.Should().NotContain("<script>", "learner text is never markup");
        html.Should().Contain("&lt;script&gt;", "it renders as the characters the learner typed");
        html.Should().Contain("&lt;b&gt;bold&lt;/b&gt;");
    }

    // ---------------------------------------------------------------- failure paths

    [Fact]
    public async Task TheQuestionStaysOnScreenWhenTheRunFails()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new HttpRequestException("offline");
        state.Draft = "Did that send?";
        await state.SendDraftAsync();

        var html = await RenderAsync(state);

        html.Should().Contain("Did that send?",
            "an error notice must not replace the learner's own words");
    }

    // ---------------------------------------------------------------- mixed turn

    [Fact]
    public async Task AMixedTurnRendersQuestionThenAnswerThenOneActionPair()
    {
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        var turn = CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: CoachStateMachineTests.Suggestion(),
            messages: [CoachAnswerStateTests.AnswerMessage(answer)],
            answer: answer);

        var html = await RenderAsync(await AfterAskingAsync("Explain this and tidy my plan", turn));

        var question = html.IndexOf("Explain this and tidy my plan", StringComparison.Ordinal);
        var answerText = html.IndexOf("은/는 marks the topic", StringComparison.Ordinal);

        question.Should().BeGreaterThan(-1);
        answerText.Should().BeGreaterThan(question, "the answer follows the question");

        Regex.Matches(html, Regex.Escape("Not now")).Count.Should().Be(1,
            "a mixed turn still offers exactly one decision");
    }
}
