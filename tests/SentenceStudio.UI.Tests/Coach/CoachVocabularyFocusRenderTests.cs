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
/// The vocabulary focus as it reaches the browser.
/// </summary>
/// <remarks>
/// The contract deliberately carries the learner's own words and nothing else — no vocabulary
/// identifiers, no due dates, no mastery. These tests hold the rendering to that same line: the
/// exact set, in the server's order, tagged with its language, and no trace of the machinery
/// that picked it.
/// </remarks>
public class CoachVocabularyFocusRenderTests
{
    // Concrete Korean action verbs, in a deliberate rank order that is not alphabetical in
    // either script — so a test that passes cannot be passing by accident of sorting.
    internal static CoachVocabularyFocusDto ActionVerbs() => new()
    {
        FocusCode = "grammar.action-verb",
        DisplayLabel = "action verbs",
        EligibleCount = 12,
        SelectedCount = 3,
        Words =
        [
            new CoachVocabularyFocusWordDto
            {
                TargetText = "달리다",
                TargetLanguageTag = "ko",
                DisplayText = "to run",
                DisplayLanguageTag = "en"
            },
            new CoachVocabularyFocusWordDto
            {
                TargetText = "가다",
                TargetLanguageTag = "ko",
                DisplayText = "to go",
                DisplayLanguageTag = "en"
            },
            new CoachVocabularyFocusWordDto
            {
                TargetText = "먹다",
                TargetLanguageTag = "ko",
                DisplayText = null,
                DisplayLanguageTag = null
            }
        ]
    };

    private static async Task<string> RenderAsync(CoachVocabularyFocusDto? focus, bool decode = true)
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
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CoachVocabularyFocus.Focus)] = focus
            });

            var output = await renderer.RenderComponentAsync<CoachVocabularyFocus>(parameters);
            var html = output.ToHtmlString();
            return decode ? System.Net.WebUtility.HtmlDecode(html) : html;
        });
    }

    // ---------------------------------------------------------------- the set itself

    [Fact]
    public async Task EverySelectedWordIsRendered()
    {
        var html = await RenderAsync(ActionVerbs());

        html.Should().Contain("달리다").And.Contain("가다").And.Contain("먹다");
    }

    [Fact]
    public async Task TheWordsKeepTheServersRankOrder()
    {
        var html = await RenderAsync(ActionVerbs());

        var first = html.IndexOf("달리다", StringComparison.Ordinal);
        var second = html.IndexOf("가다", StringComparison.Ordinal);
        var third = html.IndexOf("먹다", StringComparison.Ordinal);

        first.Should().BeLessThan(second);
        second.Should().BeLessThan(third, "the server's order is meaningful and must not be re-sorted");
    }

    [Fact]
    public async Task TheOrderIsExpressedAsAnOrderedList()
    {
        var html = await RenderAsync(ActionVerbs());

        html.Should().Contain("<ol", "an order that matters is an ordered list, not a styled bullet list");
    }

    [Fact]
    public async Task TargetTermsCarryTheirLanguageTag()
    {
        var html = await RenderAsync(ActionVerbs());

        Regex.Matches(html, "lang=\"ko\"").Count.Should().Be(3,
            "every Korean term needs its tag for font selection and speech");
    }

    [Fact]
    public async Task TheLearnersOwnGlossIsShownWithItsOwnLanguage()
    {
        var html = await RenderAsync(ActionVerbs());

        html.Should().Contain("to run");
        html.Should().Contain("lang=\"en\"", "the gloss is in the display language, not the target one");
    }

    [Fact]
    public async Task AWordWithNoGlossRendersJustTheTerm()
    {
        var html = await RenderAsync(ActionVerbs());

        // 먹다 has no translation. Nothing may be invented in its place.
        html.Should().NotContain("to eat");
    }

    // ---------------------------------------------------------------- counts

    [Fact]
    public async Task TheCountNamesBothSelectedAndEligible()
    {
        var html = await RenderAsync(ActionVerbs());

        html.Should().Contain("3 of 12 matching words");
    }

    [Fact]
    public async Task TheEligibleTotalIsOmittedWhenItAddsNothing()
    {
        var focus = ActionVerbs();
        var html = await RenderAsync(new CoachVocabularyFocusDto
        {
            FocusCode = focus.FocusCode,
            DisplayLabel = focus.DisplayLabel,
            EligibleCount = 3,
            SelectedCount = 3,
            Words = focus.Words
        });

        html.Should().Contain("3 words");
        html.Should().NotContain("of 3", "\"3 of 3\" reads like a score and tells the learner nothing");
    }

    [Fact]
    public async Task TheCountUsesNoReviewOrMasteryLanguage()
    {
        var html = await RenderAsync(ActionVerbs());

        foreach (var banned in new[] { "due", "mastery", "overdue", "review queue", "score" })
        {
            html.Should().NotContainEquivalentOf(banned,
                $"'{banned}' would present a focus as a schedule the learner owes");
        }
    }

    // ---------------------------------------------------------------- nothing leaks

    [Fact]
    public async Task NoIdentifierOrInternalCodeReachesTheDom()
    {
        var html = await RenderAsync(ActionVerbs());

        html.Should().NotContain("grammar.action-verb",
            "the canonical code is for branching, never for reading");
    }

    [Fact]
    public async Task TheLabelComesFromTheServerNotAHardcodedMap()
    {
        var focus = ActionVerbs();
        var html = await RenderAsync(new CoachVocabularyFocusDto
        {
            FocusCode = "grammar.some-future-code",
            DisplayLabel = "descriptive adjectives",
            EligibleCount = 4,
            SelectedCount = 2,
            Words = focus.Words.Take(2).ToList()
        });

        html.Should().Contain("descriptive adjectives",
            "a focus code the client has never seen must still render its label");
    }

    // ---------------------------------------------------------------- empty states

    [Fact]
    public async Task ANullFocusRendersNothingAtAll()
    {
        var html = await RenderAsync(null);

        html.Trim().Should().BeEmpty("no focus is a normal state, not an empty card");
    }

    [Fact]
    public async Task AFocusWithNoWordsAndNoSelectionRendersNothing()
    {
        // The shape a resolver returns for NoMatches / InsufficientMatches / MetadataUnavailable
        // if it ever reached a client: a label with nothing behind it.
        var html = await RenderAsync(new CoachVocabularyFocusDto
        {
            FocusCode = "grammar.action-verb",
            DisplayLabel = "action verbs",
            EligibleCount = 0,
            SelectedCount = 0,
            Words = []
        });

        html.Trim().Should().BeEmpty("an empty focus card would claim a focus the learner does not have");
    }

    // ---------------------------------------------------------------- safety

    [Fact]
    public async Task WordTextIsEscapedNotInterpreted()
    {
        var html = await RenderAsync(new CoachVocabularyFocusDto
        {
            FocusCode = "grammar.action-verb",
            DisplayLabel = "<em>action verbs</em>",
            EligibleCount = 1,
            SelectedCount = 1,
            Words =
            [
                new CoachVocabularyFocusWordDto
                {
                    TargetText = "<script>alert(1)</script>",
                    TargetLanguageTag = "ko",
                    DisplayText = "<b>bold</b>",
                    DisplayLanguageTag = "en"
                }
            ]
        }, decode: false);

        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;b&gt;bold&lt;/b&gt;");
        html.Should().Contain("&lt;em&gt;", "even the server label is text, not markup");
    }

    [Fact]
    public async Task ABlankLanguageTagDoesNotClaimAnUnknownLanguage()
    {
        var html = await RenderAsync(new CoachVocabularyFocusDto
        {
            FocusCode = "grammar.action-verb",
            DisplayLabel = "action verbs",
            EligibleCount = 1,
            SelectedCount = 1,
            Words =
            [
                new CoachVocabularyFocusWordDto
                {
                    TargetText = "가다",
                    TargetLanguageTag = "   ",
                    DisplayText = "to go",
                    DisplayLanguageTag = ""
                }
            ]
        });

        html.Should().NotContain("lang=\"\"",
            "an empty lang tells assistive tech the language is unknown, which is worse than inheriting");
    }

    // ---------------------------------------------------------------- accessibility

    [Fact]
    public async Task TheSectionIsLabelledByItsOwnHeading()
    {
        var html = await RenderAsync(ActionVerbs());

        html.Should().Contain("<section");
        html.Should().Contain("aria-labelledby=\"coach-focus-heading\"");
        html.Should().Contain("id=\"coach-focus-heading\"");
    }

    [Fact]
    public async Task TheHeadingNamesTheFocus()
    {
        var html = await RenderAsync(ActionVerbs());

        html.Should().Contain("Focus: action verbs");
    }

    [Fact]
    public async Task NoEmojiReachesTheLearner()
    {
        var html = await RenderAsync(ActionVerbs());

        html.EnumerateRunes()
            .Where(r => r.Value is >= 0x1F300 and <= 0x1FAFF)
            .Should().BeEmpty();
    }
}
