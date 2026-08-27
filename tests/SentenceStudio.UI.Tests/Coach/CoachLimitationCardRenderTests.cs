using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// What a learner actually sees when Sam says no.
/// </summary>
/// <remarks>
/// <para>
/// Three defects are in scope, and none of them looks like a bug in a screenshot.
/// </para>
/// <para>
/// <b>The false limitation.</b> A refusal rendered without the destination the server sent tells
/// the learner the app cannot do something it plainly can. They believe it and stop looking, so the
/// feature might as well not exist.
/// </para>
/// <para>
/// <b>The undisclosed consequence.</b> A link to the screen where a learner can delete their
/// vocabulary is fine. The same link without the disclosure is a trap, and the difference is one
/// element that is easy to lose in a layout change.
/// </para>
/// <para>
/// <b>The confident guess.</b> A client that maps an unrecognised code onto its nearest neighbour
/// tells the learner why Sam refused, in Sam's voice, using a reason the server never gave.
/// </para>
/// <para>
/// Host parity is asserted by rendering under both base URIs. The card reads neither the base URI
/// nor the navigation manager, so parity is a property of the file — these tests are what stops it
/// quietly becoming a property of the current implementation instead.
/// </para>
/// </remarks>
public sealed class CoachLimitationCardRenderTests
{
    private const string WebBaseUri = "https://sentencestudio.example/";
    private const string WebViewBaseUri = "app://0.0.0.0/";

    private static readonly DateTime AsOf = new(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc);

    private static CoachLimitationDto BulkDeletion() => new()
    {
        Code = CoachLimitationCode.ExceedsSafeChangeScope,
        Coverage = CoachEvidenceCoverage.CompleteOwnedSet,
        AsOfUtc = AsOf,
        AffectedCount = 412,
        Destination = CoachRouteCatalog.Build(CoachRouteName.Vocabulary),
        ExportSurface = CoachRouteCatalog.Build(CoachRouteName.Settings),
        Alternatives =
        [
            CoachAlternativeCode.ExportBeforeRemoving,
            CoachAlternativeCode.RemoveOneListAtATime,
            CoachAlternativeCode.StartAFreshList
        ]
    };

    private static CoachLimitationDto ReviewBoundary() => new()
    {
        Code = CoachLimitationCode.WouldRemoveLearningValue,
        Coverage = CoachEvidenceCoverage.SingleDay,
        AsOfUtc = AsOf,
        AffectedCount = 18,
        Alternatives = [CoachAlternativeCode.UseHintLadder, CoachAlternativeCode.TakeAShorterSession],
        HintLadder =
        [
            new CoachHintRungDto(1, CoachHintKind.Category),
            new CoachHintRungDto(2, CoachHintKind.Cloze),
            new CoachHintRungDto(3, CoachHintKind.FormCue)
        ],
        ShorterSession = new CoachShorterSessionOfferDto(6, 18, PreservesRetrieval: true)
    };

    // ── S15 in both hosts ────────────────────────────────────────────────────

    [Theory]
    [InlineData(WebBaseUri)]
    [InlineData(WebViewBaseUri)]
    public async Task S15_renders_count_alternatives_and_both_surfaces(string baseUri)
    {
        var html = await RenderAsync(BulkDeletion(), baseUri: baseUri);

        html.Should().Contain("data-coach-limitation=\"ExceedsSafeChangeScope\"");
        html.Should().Contain(
            "data-coach-limitation-count=\"412\"",
            "the consequence is a number the learner can weigh, not an adjective they can discount");

        html.Should().Contain("data-coach-limitation-destination=\"Vocabulary\"");
        html.Should().Contain(
            "data-coach-limitation-export=\"Settings\"",
            "the export screen is the one further surface S15 can honestly name; the whole-data "
            + "screen it used to claim does not exist");

        html.Should().Contain("data-coach-limitation-alternative=\"ExportBeforeRemoving\"");
        html.Should().Contain("data-coach-limitation-alternative=\"RemoveOneListAtATime\"");
        html.Should().Contain("data-coach-limitation-alternative=\"StartAFreshList\"");
    }

    /// <summary>AC-G2. Both named surfaces disclose before the learner can act on either.</summary>
    [Theory]
    [InlineData(WebBaseUri)]
    [InlineData(WebViewBaseUri)]
    public async Task S15_discloses_side_effects_for_both_surfaces(string baseUri)
    {
        var html = await RenderAsync(BulkDeletion(), baseUri: baseUri);

        // Both surfaces S15 actually names: the bounded vocabulary screen and the export screen.
        // Neither is ChangesSettings any more — Settings deletes coach conversation history, so
        // its ceiling is EditsLearnerData, and there is no whole-data screen to disclose at all.
        html.Should().Contain("data-coach-limitation-destination=\"Vocabulary\"");
        html.Should().Contain("data-coach-limitation-export=\"Settings\"");
        html.Should().Contain("data-coach-limitation-effect=\"EditsLearnerData\"");
        html.Should().NotContain("data-coach-limitation-fullscope",
            "no screen performs a whole-vocabulary start-clean, so none is named");

        html.Should().Contain(
            "You can change or delete your own data there.",
            "the disclosure is rendered text, not a data attribute a learner cannot read");
    }

    [Fact]
    public async Task S15_never_renders_a_path_or_a_query()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.ExceedsSafeChangeScope,
            AffectedCount = 412,
            Destination = CoachRouteCatalog.Build(
                CoachRouteName.Vocabulary,
                [new CoachRouteParameterDto(CoachRouteParameterKey.ResourceId, "77")])
        };

        var html = await RenderAsync(limitation);

        html.Should().NotContain("href", "W7 supplies metadata; it does not navigate");
        html.Should().NotContain("?resource", "no query string is ever composed from a parameter");
    }

    // ── S16 in both hosts ────────────────────────────────────────────────────

    [Theory]
    [InlineData(WebBaseUri)]
    [InlineData(WebViewBaseUri)]
    public async Task S16_renders_the_ladder_in_order_and_the_shorter_session(string baseUri)
    {
        var html = await RenderAsync(ReviewBoundary(), baseUri: baseUri);

        html.Should().Contain("data-coach-limitation-hint=\"Category\"");
        html.Should().Contain("data-coach-limitation-hint=\"FormCue\"");
        html.Should().Contain("data-coach-limitation-hint=\"Cloze\"");

        html.IndexOf("Category", StringComparison.Ordinal).Should().BeLessThan(
            html.IndexOf("Cloze", StringComparison.Ordinal),
            "the rungs render in ascending support, so 'a bigger nudge' means the one below it");

        html.Should().Contain("data-coach-limitation-shorter=\"6\"");
        html.Should().Contain("data-coach-limitation-shorter-full=\"18\"");
        html.Should().Contain(
            "Still full practice, just fewer words.",
            "the shorter session must read as less of the same work, not as easier work");
    }

    /// <summary>AC-S16a. The rendered card cannot contain a term because the shape cannot carry one.</summary>
    [Theory]
    [InlineData(WebBaseUri)]
    [InlineData(WebViewBaseUri)]
    public async Task S16_renders_what_a_rung_would_give_never_the_rung_itself(string baseUri)
    {
        var html = await RenderAsync(ReviewBoundary(), baseUri: baseUri);

        html.Should().Contain("What kind of word it is");
        html.Should().Contain("How it starts and how long it is");
        html.Should().Contain("The sentence with it missing");

        html.Should().NotContain(
            "data-coach-limitation-destination",
            "naming a screen would imply the answers are visible on one");
    }

    [Fact]
    public async Task S16_hides_the_shorter_session_when_the_server_withheld_it()
    {
        var full = ReviewBoundary();
        var withheld = new CoachLimitationDto
        {
            Code = full.Code,
            AffectedCount = 1,
            HintLadder = full.HintLadder,
            Alternatives = full.Alternatives,
            ShorterSession = null
        };

        var html = await RenderAsync(withheld);

        html.Should().NotContain("data-coach-limitation-shorter");
        html.Should().Contain(
            "data-coach-limitation-hint=\"Category\"",
            "the ladder stands on its own when there is nothing shorter to offer");
    }

    // ── Neutral on unknown ───────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_limitation_code_renders_a_neutral_heading()
    {
        var html = await RenderAsync(new CoachLimitationDto { Code = CoachLimitationCode.Unknown });

        html.Should().Contain("Something Sam can\u2019t do here");

        html.Should().NotContain("This isn\u2019t built yet.");
        html.Should().NotContain(
            "Sam won\u2019t do this one.",
            "the two codes most likely to be guessed between point the learner in opposite "
            + "directions, so an unrecognised one gets no reason at all");
    }

    [Fact]
    public async Task Unknown_route_renders_no_destination()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AvailableOnAnotherSurface,
            Destination = new CoachDestinationDto(CoachRouteName.Unknown, [], CoachRouteSideEffect.None)
        };

        var html = await RenderAsync(limitation);

        html.Should().NotContain(
            "data-coach-limitation-destination",
            "a route this build cannot resolve is dropped, never guessed at and never rendered "
            + "blank — the learner would tap a screen that does not exist here");
    }

    /// <summary>
    /// The one unknown that must be a sentence rather than silence.
    /// </summary>
    [Fact]
    public async Task Unknown_side_effect_renders_a_stated_non_answer_not_safety()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AvailableOnAnotherSurface,
            Destination = new CoachDestinationDto(
                CoachRouteName.Vocabulary,
                [],
                CoachRouteSideEffect.Unknown)
        };

        var html = await RenderAsync(limitation);

        html.Should().Contain("Consequences not stated.");
        html.Should().NotContain(
            "Nothing changes",
            "an unrecognised consequence must never read as a read-only screen; the one case this "
            + "field exists for is the screen that is not");
    }

    [Fact]
    public async Task Unknown_alternative_is_dropped()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.ExceedsSafeChangeScope,
            Alternatives = [CoachAlternativeCode.Unknown, CoachAlternativeCode.RemoveOneListAtATime]
        };

        var html = await RenderAsync(limitation);

        html.Should().NotContain("data-coach-limitation-alternative=\"Unknown\"");
        html.Should().Contain("data-coach-limitation-alternative=\"RemoveOneListAtATime\"");
    }

    [Fact]
    public async Task Unknown_hint_rung_renders_as_unavailable()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.WouldRemoveLearningValue,
            HintLadder = [new CoachHintRungDto(1, CoachHintKind.Unknown)]
        };

        var html = await RenderAsync(limitation);

        html.Should().Contain("Not available");
        html.Should().NotContain(
            "The sentence with it missing",
            "an unrecognised rung must never fall back to a neighbour; on this ladder the "
            + "neighbour above the top is the answer");
    }

    [Fact]
    public async Task Null_limitation_renders_nothing()
    {
        var html = await RenderAsync(null);

        html.Trim().Should().BeEmpty();
    }

    // ── Host parity and localization ─────────────────────────────────────────

    /// <summary>
    /// Byte-identical markup on both hosts, for both scenarios.
    /// </summary>
    /// <remarks>
    /// The card injects no navigation manager and reads no platform flag, so this passes by
    /// construction today. It is asserted anyway: the next person to add a "just on mobile" branch
    /// finds out here rather than in a divergence nobody notices for a release.
    /// </remarks>
    [Fact]
    public async Task Both_hosts_render_identical_markup()
    {
        foreach (var limitation in new[] { BulkDeletion(), ReviewBoundary() })
        {
            var web = await RenderAsync(limitation, baseUri: WebBaseUri);
            var webView = await RenderAsync(limitation, baseUri: WebViewBaseUri);

            web.Should().NotBeEmpty();
            webView.Should().Be(
                web,
                "the limitation card is host-agnostic; a divergence means somebody added a "
                + "platform branch to a surface whose entire job is telling the truth consistently");
        }
    }

    [Fact]
    public async Task Korean_renders_translated_copy_with_the_same_structure()
    {
        var english = await RenderAsync(BulkDeletion(), culture: "en");
        var korean = await RenderAsync(BulkDeletion(), culture: "ko");

        korean.Should().Contain("data-coach-limitation=\"ExceedsSafeChangeScope\"");
        korean.Should().Contain("data-coach-limitation-count=\"412\"");
        korean.Should().Contain("data-coach-limitation-effect=\"EditsLearnerData\"");

        korean.Should().NotBe(english, "the Korean card must actually be translated, not fall back");
        korean.Should().NotContain(
            "You can change or delete your own data there.",
            "an untranslated disclosure is the one line a Korean learner most needs to read");
        korean.Should().Contain("\uB2E8\uC5B4\uC7A5");
    }

    [Fact]
    public async Task Korean_translates_the_public_publication_disclosure()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AvailableOnAnotherSurface,
            Destination = CoachRouteCatalog.Build(CoachRouteName.Feedback)
        };

        var korean = await RenderAsync(limitation, culture: "ko");

        korean.Should().Contain("data-coach-limitation-effect=\"PublishesPublicly\"");
        korean.Should().NotContain(
            "What you send becomes public",
            "the strongest disclosure in the catalogue must not be the one that falls back to "
            + "English");
    }

    // ── Adversarial: nothing renders learner or model text ───────────────────

    /// <summary>
    /// The single string on the whole destination graph must never reach the screen as text.
    /// </summary>
    /// <remarks>
    /// <c>CoachRouteParameterDto.Value</c> is the only string a destination carries, and it exists
    /// to be a server-owned identifier. If the card ever rendered it, that field would become the
    /// one place a term, a gloss, or a model-composed query could arrive on a surface whose entire
    /// premise is that it carries no content. The value below is deliberately a Korean term and a
    /// query string at once — if either appears, the leak is real regardless of how it got there.
    /// </remarks>
    [Theory]
    [InlineData(WebBaseUri)]
    [InlineData(WebViewBaseUri)]
    public async Task Parameter_values_are_never_rendered_as_text(string baseUri)
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AvailableOnAnotherSurface,
            Destination = CoachRouteCatalog.Build(
                CoachRouteName.Vocabulary,
                [new CoachRouteParameterDto(CoachRouteParameterKey.VocabularyWordId, "\uC0AC\uACFC?answer=apple")])
        };

        var html = await RenderAsync(limitation, baseUri: baseUri);

        html.Should().NotContain("\uC0AC\uACFC", "a parameter value is an identifier, never rendered text");
        html.Should().NotContain("answer=apple");
        html.Should().Contain(
            "data-coach-limitation-destination=\"Vocabulary\"",
            "the destination still renders; it is the value that stays off the screen");
    }

    /// <summary>
    /// A limitation assembled to look like S16 but carrying a destination still leaks nothing.
    /// </summary>
    [Fact]
    public async Task A_hint_ladder_with_a_destination_still_renders_no_content()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.WouldRemoveLearningValue,
            HintLadder =
            [
                new CoachHintRungDto(1, CoachHintKind.Category),
                new CoachHintRungDto(2, CoachHintKind.Cloze)
            ],
            Destination = CoachRouteCatalog.Build(
                CoachRouteName.Vocabulary,
                [new CoachRouteParameterDto(CoachRouteParameterKey.VocabularyWordId, "\uBA39\uB2E4")])
        };

        var html = await RenderAsync(limitation);

        html.Should().NotContain("\uBA39\uB2E4");
        html.Should().Contain("What kind of word it is");
        html.Should().Contain(
            "The sentence with it missing",
            "the rung renders what it would give, which is never the thing it would give it about");
    }

    /// <summary>
    /// A count is rendered; a count is not narrated. The card does no arithmetic.
    /// </summary>
    [Fact]
    public async Task The_card_renders_server_counts_and_derives_none()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.WouldRemoveLearningValue,
            AffectedCount = 18,
            ShorterSession = new CoachShorterSessionOfferDto(6, 18, PreservesRetrieval: true)
        };

        var html = await RenderAsync(limitation);

        html.Should().Contain("data-coach-limitation-count=\"18\"");
        html.Should().Contain("data-coach-limitation-shorter=\"6\"");

        html.Should().NotContain("12", "18 minus 6 is a number the server never sent");
        html.Should().NotContain("%", "a percentage is a derived claim, and derived claims go stale");
        html.Should().NotContain("most of", "a quantifier the server did not supply is an invented one");
    }

    private static async Task<string> RenderAsync(
        CoachLimitationDto? limitation,
        string culture = "en",
        string baseUri = WebBaseUri)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

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
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// Supplies the base URI both hosts are distinguished by, so a card that reads it would render
    /// differently and the parity test would catch it.
    /// </summary>
    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException(
                "W7 supplies limitation metadata and never navigates. A navigation from this card "
                + "would be an unauthorized one under AC-G1.");
    }

    // ================================================================ R5 regression: AnswerShapeInvalid rendering

    /// <summary>
    /// R5-5: The limitation card for AnswerShapeInvalid renders the correct data attribute,
    /// resolves exact English and Korean copy, carries no destination/evidence/count, and does
    /// not fabricate an assistant bubble.
    /// </summary>
    [Fact]
    public async Task AnswerShapeInvalid_renders_data_attribute_and_no_assistant_bubble()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AnswerShapeInvalid,
            Coverage = CoachEvidenceCoverage.Unknown,
            AsOfUtc = new DateTime(2026, 8, 22, 14, 30, 0, DateTimeKind.Utc)
        };

        var html = await RenderAsync(limitation, "en", WebBaseUri);

        html.Should().Contain("data-coach-limitation=\"AnswerShapeInvalid\"",
            "the card data attribute identifies the specific limitation code for automation/tests");

        // No destination, evidence, or count fields should appear.
        html.Should().NotContain("data-coach-limitation-destination",
            "AnswerShapeInvalid carries no destination");
        html.Should().NotContain("data-coach-limitation-evidence",
            "AnswerShapeInvalid carries no evidence scope");
        html.Should().NotContain("data-coach-limitation-count",
            "AnswerShapeInvalid carries no affected count");

        // No assistant bubble should be fabricated.
        html.Should().NotContain("data-coach-bubble",
            "a shape refusal is silent; the limitation card is the only visible outcome");
    }

    [Fact]
    public async Task AnswerShapeInvalid_resolves_exact_english_copy()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AnswerShapeInvalid,
            Coverage = CoachEvidenceCoverage.Unknown
        };

        var html = await RenderAsync(limitation, "en", WebBaseUri);

        html.Should().Contain("Sam could not finish that answer",
            "the English resource string must render exactly as shipped by Kaylee (R4)");
    }

    [Fact]
    public async Task AnswerShapeInvalid_resolves_exact_korean_copy()
    {
        var limitation = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AnswerShapeInvalid,
            Coverage = CoachEvidenceCoverage.Unknown
        };

        var html = await RenderAsync(limitation, "ko", WebBaseUri);

        html.Should().Contain("\uC324\uC774 \uADF8 \uB2F5\uBCC0\uC744 \uC644\uC131\uD558\uC9C0 \uBABB\uD588\uC5B4\uC694",
            "the Korean resource string must render exactly as shipped by Kaylee (R4)");
    }
}
