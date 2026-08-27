using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// What a learner using a screen reader gets from a refusal, and what the card must never say.
/// </summary>
/// <remarks>
/// <para>
/// The render tests next door check that the right words appear. These check that the words are
/// reachable in the right order by somebody who cannot see the layout, and that three specific
/// non-facts never appear: a count of zero, a coverage the server did not state, and a timestamp
/// the server did not send.
/// </para>
/// <para>
/// <b>Why association matters more here than on most cards.</b> This card renders two lists whose
/// meaning is carried entirely by the line above them. "Archive instead of delete" is advice under
/// "Instead, you could" and an instruction under "Nudges you can ask for". A screen reader that
/// announces the list without its heading gives the learner the items with the meaning stripped
/// off, and on this card the two lists are the safe path and the hint path.
/// </para>
/// </remarks>
public class CoachLimitationCardAccessibilityTests
{
    private const string WebBaseUri = "https://sentencestudio.example/";
    private const string WebViewBaseUri = "app://0.0.0.0/";

    // AC-S15 / AC-S16: the region carries an accessible name, taken from the refusal itself.
    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public async Task The_refusal_is_a_named_region(string culture)
    {
        var html = await RenderAsync(BulkDeletion(), culture);

        var section = Match(html, "<section class=\"coach-limitation\"[^>]*>", "the card's section");
        section.Should().Contain("aria-labelledby=\"coach-limitation-reason\"",
            "a section with no accessible name is not a region a screen reader can navigate to");

        html.Should().Contain("id=\"coach-limitation-reason\"",
            "the name must point at an element that exists, or the region is named after nothing");
    }

    // AC-S15: the alternatives list is announced under the line that gives it its meaning.
    [Fact]
    public async Task The_alternatives_list_is_associated_with_its_heading()
    {
        var html = await RenderAsync(BulkDeletion());

        html.Should().Contain("id=\"coach-limitation-alternatives-heading\"");
        Match(html, "<ul class=\"coach-limitation-alternatives[^\"]*\"[^>]*>", "the alternatives list")
            .Should().Contain("aria-labelledby=\"coach-limitation-alternatives-heading\"");
    }

    // AC-S16b: same for the ladder, whose items are meaningless without "you can ask for".
    [Fact]
    public async Task The_hint_ladder_is_associated_with_its_heading()
    {
        var html = await RenderAsync(HintLadder());

        html.Should().Contain("id=\"coach-limitation-hints-heading\"");
        Match(html, "<ol class=\"coach-limitation-hints[^\"]*\"[^>]*>", "the hint ladder")
            .Should().Contain("aria-labelledby=\"coach-limitation-hints-heading\"");
    }

    // AC-S15: "affects 0 words" beside a refusal reads as a refusal about nothing.
    [Fact]
    public async Task A_zero_affected_count_is_not_stated()
    {
        var html = await RenderAsync(BulkDeletion(affectedCount: 0));

        html.Should().NotContain("data-coach-limitation-count",
            "a count of zero invites the learner to retry the thing that was just refused");
        html.Should().Contain("data-coach-limitation=\"ExceedsSafeChangeScope\"",
            "suppressing the count must not suppress the refusal");
    }

    [Fact]
    public async Task An_uncounted_refusal_states_no_count()
    {
        var html = await RenderAsync(BulkDeletion(affectedCount: null));

        html.Should().NotContain("data-coach-limitation-count",
            "the server declining to count is not the server counting none");
    }

    [Fact]
    public async Task A_real_count_is_still_stated()
    {
        var html = await RenderAsync(BulkDeletion(affectedCount: 412));

        html.Should().Contain("data-coach-limitation-count=\"412\"",
            "suppressing zero must not suppress the number the refusal is actually about");
    }

    // AC-S15: coverage, window and as-of are the server's or they are absent.
    [Fact]
    public async Task Unstated_scope_is_absent_rather_than_guessed()
    {
        var html = await RenderAsync(new CoachLimitationDto
        {
            Code = CoachLimitationCode.RefusedByDesign,
            Coverage = CoachEvidenceCoverage.Unknown
        });

        html.Should().NotContain("data-coach-limitation-coverage");
        html.Should().NotContain("data-coach-limitation-window");
        html.Should().NotContain("data-coach-limitation-asof");
    }

    [Fact]
    public async Task Stated_scope_is_rendered_from_the_server_values()
    {
        var html = await RenderAsync(BulkDeletion(
            coverage: CoachEvidenceCoverage.CompleteOwnedSet,
            windowStart: new DateOnly(2026, 1, 1),
            windowEnd: new DateOnly(2026, 1, 7),
            asOfUtc: new DateTime(2026, 1, 7, 9, 30, 0, DateTimeKind.Utc)));

        html.Should().Contain("data-coach-limitation-coverage=\"CompleteOwnedSet\"");
        html.Should().Contain("data-coach-limitation-window");
        html.Should().Contain("data-coach-limitation-asof=\"2026-01-07T09:30:00.0000000Z\"");
    }

    // AC-S15: partial coverage is stated as partial, never rounded up to complete.
    [Fact]
    public async Task Partial_coverage_is_not_reported_as_complete()
    {
        var page = await RenderAsync(BulkDeletion(coverage: CoachEvidenceCoverage.PageOfOwnedSet));
        var complete = await RenderAsync(BulkDeletion(coverage: CoachEvidenceCoverage.CompleteOwnedSet));

        page.Should().Contain("data-coach-limitation-coverage=\"PageOfOwnedSet\"");
        page.Should().NotBe(complete, "a page of the set must not read exactly like the whole set");
    }

    // AC-S15 / AC-S16a: unknown wire values render neutrally, in both directions.
    [Fact]
    public async Task An_unknown_limitation_code_gets_a_neutral_heading_and_no_borrowed_reason()
    {
        var html = await RenderAsync(new CoachLimitationDto { Code = (CoachLimitationCode)9999 });

        html.Should().Contain("Something Sam can’t do here");
        foreach (var borrowed in new[]
        {
            "This isn’t built yet.",
            "Sam won’t do this one.",
            "You can do this yourself on another screen.",
            "This would take away the practice it’s meant to give.",
            "This is more than Sam will change in one step."
        })
        {
            html.Should().NotContain(borrowed, "an unknown code must not borrow a known code's reason");
        }
    }

    [Fact]
    public async Task An_unknown_route_offers_no_destination_at_all()
    {
        var html = await RenderAsync(new CoachLimitationDto
        {
            Code = CoachLimitationCode.AvailableOnAnotherSurface,
            Destination = new CoachDestinationDto((CoachRouteName)9999, [], CoachRouteSideEffect.EditsLearnerData)
        });

        html.Should().NotContain("data-coach-limitation-destination",
            "a route this build cannot resolve must not become a link the learner cannot follow");
    }

    [Fact]
    public async Task An_unknown_side_effect_is_stated_rather_than_read_as_harmless()
    {
        var html = await RenderAsync(new CoachLimitationDto
        {
            Code = CoachLimitationCode.AvailableOnAnotherSurface,
            Destination = new CoachDestinationDto(CoachRouteName.Vocabulary, [], (CoachRouteSideEffect)9999)
        });

        html.Should().Contain("data-coach-limitation-destination=\"Vocabulary\"");
        html.Should().NotContain("Nothing changes",
            "an unknown consequence must not render as the copy for no consequence");
    }

    // AC-S16b: an unknown rung must not fall back to the rung above it, which is the answer.
    [Fact]
    public async Task An_unknown_hint_rung_does_not_fall_back_to_a_neighbour()
    {
        var html = await RenderAsync(new CoachLimitationDto
        {
            Code = CoachLimitationCode.WouldRemoveLearningValue,
            HintLadder = [new CoachHintRungDto(1, (CoachHintKind)9999)]
        });

        var known = await RenderAsync(HintLadder());
        html.Should().NotContain(VisibleOf(known, "data-coach-limitation-hint=\"Cloze\""),
            "the neighbour above the top rung is the answer itself");
    }

    // No emoji anywhere in the learner-visible copy, in either language.
    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public async Task The_card_renders_no_emoji(string culture)
    {
        var html = await RenderAsync(Everything(), culture);

        // Same rune ranges as the theme swatch's house-rule check, so the two agree on what an
        // emoji is. Korean sits at U+AC00-U+D7AF and U+3130-U+318F and is untouched by them.
        var emoji = html.EnumerateRunes()
            .Where(r => r.Value is (>= 0x1F300 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or 0xFE0F)
            .Select(r => r.ToString())
            .ToList();

        emoji.Should().BeEmpty("this app uses Bootstrap icons or plain text, never emoji");
        html.Should().Contain("coach-limitation-reason", "the scan must be over a card that rendered");
    }

    // Host parity is claimed in the component's own header. This is the check.
    [Fact]
    public async Task Both_hosts_render_byte_identical_markup()
    {
        var web = await RenderAsync(Everything(), baseUri: WebBaseUri);
        var webView = await RenderAsync(Everything(), baseUri: WebViewBaseUri);

        web.Should().Be(webView, "the card reads no base URI, so the two hosts cannot differ");
        web.Should().Contain("aria-labelledby", "the parity assertion must be over real markup");
    }

    private static string VisibleOf(string html, string marker)
    {
        var index = html.IndexOf(marker, StringComparison.Ordinal);
        index.Should().BeGreaterThan(-1, $"'{marker}' must be present for this comparison to mean anything");

        var open = html.IndexOf('>', index) + 1;
        var close = html.IndexOf('<', open);
        return html[open..close].Trim();
    }

    private static string Match(string html, string pattern, string what)
    {
        var match = Regex.Match(html, pattern);
        match.Success.Should().BeTrue($"{what} must be present, or this test asserts nothing");
        return match.Value;
    }

    private static CoachLimitationDto BulkDeletion(
        int? affectedCount = 412,
        CoachEvidenceCoverage coverage = CoachEvidenceCoverage.Unknown,
        DateOnly? windowStart = null,
        DateOnly? windowEnd = null,
        DateTime? asOfUtc = null) => new()
    {
        Code = CoachLimitationCode.ExceedsSafeChangeScope,
        AffectedCount = affectedCount,
        Coverage = coverage,
        WindowStartDate = windowStart,
        WindowEndDate = windowEnd,
        AsOfUtc = asOfUtc,
        Alternatives =
        [
            CoachAlternativeCode.RemoveOneListAtATime,
            CoachAlternativeCode.ExportBeforeRemoving
        ]
    };

    private static CoachLimitationDto HintLadder() => new()
    {
        Code = CoachLimitationCode.WouldRemoveLearningValue,
        HintLadder =
        [
            new CoachHintRungDto(1, CoachHintKind.Category),
            new CoachHintRungDto(2, CoachHintKind.Cloze),
            new CoachHintRungDto(3, CoachHintKind.FormCue)
        ]
    };

    private static CoachLimitationDto Everything() => new()
    {
        Code = CoachLimitationCode.WouldRemoveLearningValue,
        AffectedCount = 12,
        Coverage = CoachEvidenceCoverage.CompleteOwnedSet,
        WindowStartDate = new DateOnly(2026, 1, 1),
        WindowEndDate = new DateOnly(2026, 1, 7),
        AsOfUtc = new DateTime(2026, 1, 7, 9, 30, 0, DateTimeKind.Utc),
        Destination = CoachRouteCatalog.Build(CoachRouteName.Vocabulary),
        Alternatives = [CoachAlternativeCode.RemoveOneListAtATime, CoachAlternativeCode.TakeAShorterSession],
        HintLadder =
        [
            new CoachHintRungDto(1, CoachHintKind.Category),
            new CoachHintRungDto(2, CoachHintKind.Cloze),
            new CoachHintRungDto(3, CoachHintKind.FormCue)
        ],
        ShorterSession = new CoachShorterSessionOfferDto(5, 20, true)
    };

    private static async Task<string> RenderAsync(
        CoachLimitationDto? limitation,
        string culture = "en",
        string baseUri = WebBaseUri)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddSingleton<NavigationManager>(new StubNavigationManager(baseUri));

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<CoachLimitationCard>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(CoachLimitationCard.Limitation)] = limitation
                    }));

                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
