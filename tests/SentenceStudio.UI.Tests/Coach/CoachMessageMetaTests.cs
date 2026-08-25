using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Progress;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Message metadata and actions: when something was said, and copying what Sam said.
/// </summary>
public class CoachMessageMetaTests
{
    private static CoachMessageDto Reply(string id, string text) => new()
    {
        MessageId = id,
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Text,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static (Microsoft.Extensions.DependencyInjection.ServiceProvider Provider, StubJSRuntime Js)
        Provider(CoachWorkspaceState state)
    {
        var js = new StubJSRuntime();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => js);
        services.AddScoped(_ => state);
        services.AddScoped<IProgressService>(_ => new StubProgressService());
        return (services.BuildServiceProvider(), js);
    }

    private static async Task<string> RenderAsync<TComponent>(CoachWorkspaceState state)
        where TComponent : IComponent
    {
        var (provider, _) = Provider(state);

        await using (provider)
        await using (var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>()))
        {
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
    }

    private static async Task<CoachWorkspaceState> AfterTurnAsync(
        CoachAnswerDto? answer = null,
        string question = "how do I use the topic marker?")
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => answer is null
            ? CoachStateMachineTests.Turn(messages: [Reply("m-1", "Use the topic marker.")])
            : CoachStateMachineTests.Turn(
                messages: [CoachAnswerStateTests.AnswerMessage(answer)],
                answer: answer);

        state.Draft = question;
        await state.SendDraftAsync();
        return state;
    }

    // ---------------------------------------------------------------- timestamps

    [Fact]
    public async Task BothSpeakersGetAVisibleTimestamp()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        Regex.Matches(html, "coach-timestamp").Count.Should().Be(2,
            "the learner's question and Sam's reply are both placed in time");
    }

    [Fact]
    public async Task TheCompactTimeCarriesAFullAccessibleDateTime()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        html.Should().Contain("aria-label=\"Sent ", "the short form is never the only form available");
        html.Should().Contain("title=\"", "a pointer user gets the full value too");
    }

    [Fact]
    public async Task TheTimestampIsAMachineReadableTimeElement()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        html.Should().Contain("<time");
        html.Should().MatchRegex("datetime=\"\\d{4}-\\d{2}-\\d{2}T",
            "the ISO value is what a machine reads, independent of the display format");
    }

    [Fact]
    public async Task QuickControlsCarryNoTimestamp()
    {
        // Starter chips are controls, not utterances. Nothing was said at a time.
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var html = await RenderAsync<CoachChatPane>(state);

        html.Should().Contain("coach-chip-row");
        html.Should().NotContain("coach-timestamp");
    }

    // ---------------------------------------------------------------- copy

    [Fact]
    public async Task SamsReplyOffersACopyAction()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        Regex.Matches(html, "coach-copy").Count.Should().BeGreaterThan(0);
        html.Should().Contain("aria-label=\"Copy message\"");
    }

    [Fact]
    public async Task TheLearnersOwnMessageDoesNotOfferCopy()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        Regex.Matches(html, "aria-label=\"Copy message\"").Count.Should().Be(1,
            "only Sam's reply is worth copying; the learner already has their own words");
    }

    [Fact]
    public async Task APlainReplyCopiesItsText()
    {
        var state = await AfterTurnAsync();

        var entry = state.Timeline.Single(e => e.Kind == CoachTimelineKind.CoachMessage);
        entry.ReadableText().Should().Be("Use the topic marker.");
    }

    [Fact]
    public async Task AStructuredAnswerCopiesEveryBlockInReadingOrder()
    {
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        var state = await AfterTurnAsync(answer);

        var text = state.Timeline.Single(e => e.Kind == CoachTimelineKind.CoachMessage).ReadableText();

        text.Should().Contain("은/는 marks the topic");
        text.Should().Contain("저는 학생이에요");
        text.Should().Contain("제가 했어요");

        text.IndexOf("저는 학생이에요", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("제가 했어요", StringComparison.Ordinal),
                "a copy reads in the same order as the screen");
    }

    [Fact]
    public async Task ACopyCarriesNoIdentifiersOrMarkup()
    {
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        var state = await AfterTurnAsync(answer);

        var text = state.Timeline.Single(e => e.Kind == CoachTimelineKind.CoachMessage).ReadableText();

        text.Should().NotContain("<").And.NotContain("lang=");
        text.Should().NotContain("m-1", "message ids are not part of what was said");
    }

    [Fact]
    public async Task CopiedTextIsTheLearnerFacingTextNotEscapedMarkup()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages: [Reply("m-1", "Use <b>this</b> form.")]);

        state.Draft = "which form?";
        await state.SendDraftAsync();

        var text = state.Timeline.Single(e => e.Kind == CoachTimelineKind.CoachMessage).ReadableText();

        text.Should().Be("Use <b>this</b> form.",
            "the clipboard gets exactly the characters the learner saw, not HTML entities");
    }

    [Fact]
    public async Task AnEmptyAnswerFallsBackToThePlainText()
    {
        var answer = new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.Grammar,
            PlainText = "Just the fallback.",
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en",
            Blocks = []
        };

        var state = await AfterTurnAsync(answer);
        var text = state.Timeline.Single(e => e.Kind == CoachTimelineKind.CoachMessage).ReadableText();

        text.Should().Be("Just the fallback.");
    }

    [Fact]
    public async Task TheCopyFeedbackIsPoliteNotAnAlert()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        html.Should().Contain("aria-live=\"polite\"");
        html.Should().NotContain("role=\"alert\"",
            "a copy that did not happen has broken nothing and must not interrupt");
    }

    // ---------------------------------------------------------------- quick controls placement

    [Fact]
    public async Task StarterChipsAppearInAnEmptyConversation()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var html = await RenderAsync<CoachChatPane>(state);

        html.Should().Contain("coach-chip-row");
        html.Should().Contain("Start with a quick change");
    }

    [Fact]
    public async Task StarterChipsLeaveTheChatOnceTheLearnerHasSpoken()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        html.Should().NotContain("coach-chip-row");
    }

    [Fact]
    public async Task TheChipsMoveToThePlanCanvasAndStayFunctional()
    {
        var state = await AfterTurnAsync();
        var html = await RenderAsync<CoachPlanCanvas>(state);

        html.Should().Contain("coach-chip-row", "they are plan controls now, in the plan pane");
        html.Should().Contain("<button", "and they are still real controls, not a static list");
    }

    [Fact]
    public async Task ThePlanCanvasHasNoChipsBeforeTheConversationStarts()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var html = await RenderAsync<CoachPlanCanvas>(state);

        html.Should().NotContain("coach-chip-row",
            "they are starter prompts in the chat at that point, and must not appear twice");
    }

    [Fact]
    public async Task TheChipsAreNeverInBothPlacesAtOnce()
    {
        foreach (var started in new[] { false, true })
        {
            var state = started
                ? await AfterTurnAsync()
                : await NewSessionAsync();

            var chat = await RenderAsync<CoachChatPane>(state);
            var canvas = await RenderAsync<CoachPlanCanvas>(state);

            (chat.Contains("coach-chip-row") && canvas.Contains("coach-chip-row"))
                .Should().BeFalse($"duplicated controls when started={started}");

            (chat.Contains("coach-chip-row") || canvas.Contains("coach-chip-row"))
                .Should().BeTrue($"the controls must exist somewhere when started={started}");
        }
    }

    private static async Task<CoachWorkspaceState> NewSessionAsync()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);
        return state;
    }

    // ---------------------------------------------------------------- preserved invariants

    [Fact]
    public async Task SamAndTheLearnerKeepTheirNames()
    {
        var html = await RenderAsync<CoachChatPane>(await AfterTurnAsync());

        html.Should().Contain(">Sam<").And.Contain(">You<");
        html.Should().Contain("Conversation with Sam");
    }
}
