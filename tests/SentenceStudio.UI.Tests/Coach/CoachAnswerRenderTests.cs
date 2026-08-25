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
/// Renders <see cref="CoachAnswer"/> to real HTML: block order, language tagging, and safe
/// handling of malformed answers.
/// </summary>
/// <remarks>
/// Language tagging is the part that cannot be checked any other way. Korean text inside an
/// English answer needs <c>lang="ko"</c> or a screen reader pronounces it with an English voice,
/// which for a language-learning app is not a cosmetic problem.
/// </remarks>
public class CoachAnswerRenderTests
{
    private static async Task<string> RenderAsync(CoachAnswerDto? answer, string culture = "en")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());

        await using var provider = services.BuildServiceProvider();
        var localize = provider.GetRequiredService<BlazorLocalizationService>();
        localize.SetCulture(culture);

        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(provider, loggerFactory);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachAnswer>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Answer"] = answer }));
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    // ---------------------------------------------------------------- language tagging

    [Fact]
    public async Task KoreanSpansCarryLangKo()
    {
        var html = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer());

        html.Should().Contain("lang=\"ko\"", "target-language text must switch the screen reader voice");
        html.Should().Contain("저는 학생이에요");
    }

    [Fact]
    public async Task DisplayLanguageSpansCarryTheDisplayTag()
    {
        var html = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer());

        html.Should().Contain("lang=\"en\"");
    }

    [Fact]
    public async Task ASpanWithNoTagInheritsTheAnswersLanguageRatherThanEmittingLangEmpty()
    {
        // lang="" tells assistive tech the language is UNKNOWN, which is worse than inheriting.
        var answer = new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.Vocabulary,
            PlainText = "fallback",
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en",
            Blocks =
            [
                CoachAnswerStateTests.Block(CoachAnswerBlockKind.Answer,
                    new CoachAnswerSpanDto { Text = "밥", Language = CoachLanguageRole.Target, LanguageTag = "   " })
            ]
        };

        var html = await RenderAsync(answer);

        html.Should().NotContain("lang=\"\"");
        html.Should().Contain("lang=\"ko\"", "a target span falls back to the answer's target tag");
    }

    // ---------------------------------------------------------------- order and grouping

    [Fact]
    public async Task BlocksRenderInServerOrderWithTheDirectAnswerFirst()
    {
        var html = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer());

        var answerAt = html.IndexOf("은/는 marks the topic", StringComparison.Ordinal);
        var contrastAt = html.IndexOf("저는 학생이에요", StringComparison.Ordinal);
        var exampleAt = html.IndexOf("제가 했어요", StringComparison.Ordinal);

        answerAt.Should().BeGreaterThan(-1);
        answerAt.Should().BeLessThan(contrastAt, "the direct answer leads");
        contrastAt.Should().BeLessThan(exampleAt, "server order is preserved");
    }

    [Fact]
    public async Task TheDirectAnswerCarriesNoRedundantLabel()
    {
        var html = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer());

        html.Should().Contain("coach-answer-block-answer");
        html.Should().Contain("Comparison", "supporting blocks are labelled");
        html.Should().Contain("Example");
    }

    [Fact]
    public async Task BlockTypeIsCarriedByTextAndIconNotColourAlone()
    {
        var html = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer());

        html.Should().Contain("coach-answer-block-label", "each supporting block has a text label");
        Regex.Matches(html, "<i class=\"bi ").Count.Should().BeGreaterThan(0, "and an icon beside it");
    }

    [Fact]
    public async Task LabelsAreLocalized()
    {
        var korean = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer(), culture: "ko");

        korean.Should().Contain("예문", "block labels follow the display language");
        korean.Should().NotContain("Comparison");
    }

    // ---------------------------------------------------------------- malformed input

    [Fact]
    public async Task ANullAnswerRendersNothing()
    {
        var html = await RenderAsync(null);

        html.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnswerWithNoBlocksFallsBackToPlainText()
    {
        var answer = new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.Other,
            PlainText = "Plain fallback answer.",
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en",
            Blocks = []
        };

        var html = await RenderAsync(answer);

        html.Should().Contain("Plain fallback answer.");
    }

    [Fact]
    public async Task BlocksWithOnlyEmptySpansFallBackToPlainTextInsteadOfRenderingAnEmptyShell()
    {
        var answer = new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.Other,
            PlainText = "Plain fallback answer.",
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en",
            Blocks =
            [
                CoachAnswerStateTests.Block(CoachAnswerBlockKind.Answer,
                    new CoachAnswerSpanDto { Text = "   ", Language = CoachLanguageRole.Display, LanguageTag = "en" })
            ]
        };

        var html = await RenderAsync(answer);

        html.Should().Contain("Plain fallback answer.");
    }

    [Fact]
    public async Task AnUnusableBlockIsSkippedWhileTheRestStillRenders()
    {
        var answer = new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.Grammar,
            PlainText = "fallback",
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en",
            Blocks =
            [
                CoachAnswerStateTests.Block(CoachAnswerBlockKind.Answer,
                    CoachAnswerStateTests.Span("Real answer.", CoachLanguageRole.Display, "en")),
                new CoachAnswerBlockDto { Kind = CoachAnswerBlockKind.Note, Spans = [] }
            ]
        };

        var html = await RenderAsync(answer);

        html.Should().Contain("Real answer.");
        html.Should().NotContain("fallback", "the usable block means the fallback is not needed");
        html.Should().NotContain("coach-answer-block-note", "an empty block is skipped, not rendered hollow");
    }

    [Fact]
    public async Task ModelAuthoredTextIsEscapedNotInterpretedAsMarkup()
    {
        var answer = new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.Other,
            PlainText = "plain",
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en",
            Blocks =
            [
                CoachAnswerStateTests.Block(CoachAnswerBlockKind.Answer,
                    CoachAnswerStateTests.Span("<script>alert(1)</script>", CoachLanguageRole.Display, "en"))
            ]
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        // Deliberately NOT decoded: the raw output must show the tag escaped.
        var raw = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachAnswer>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Answer"] = answer }));
            return output.ToHtmlString();
        });

        raw.Should().NotContain("<script>", "model text is never treated as markup");
        raw.Should().Contain("&lt;script&gt;");
    }

    // ---------------------------------------------------------------- accessibility

    [Fact]
    public async Task TheAnswerIsASingleNamedGroup()
    {
        var html = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer());

        Regex.Matches(html, "role=\"group\"").Count.Should().Be(1);
        html.Should().Contain("aria-label=\"Answer\"");
    }

    [Fact]
    public async Task NoEmojiAppearInRenderedAnswers()
    {
        var html = await RenderAsync(CoachAnswerStateTests.KoreanContrastAnswer());

        Regex.IsMatch(html, @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF\uFE0F]")
            .Should().BeFalse("iconography is Bootstrap Icons only");
    }
}
