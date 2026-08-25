using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// What the learner is told about how a piece of evidence was obtained.
/// </summary>
/// <remarks>
/// <para>
/// The defect these prevent has no visible symptom. "You have 20 resources" and "here are 20 of
/// your 84" render from the same twenty rows, and until the scope line existed there was nothing
/// on screen that distinguished them — so a learner reading the first sentence supplied the second
/// one's context themselves, and supplied it wrong. Every assertion below is about a sentence that
/// is either present and true, or absent.
/// </para>
/// <para>
/// The unknown cases matter as much as the known ones. A tolerant client turns a value it cannot
/// name into <c>Unknown</c>, and an <c>Unknown</c> rendered as a guess would be a confident false
/// claim about the learner's own data. It has to render as nothing at all.
/// </para>
/// </remarks>
public class CoachEvidenceScopeRenderTests
{
    private static async Task<string> RenderAsync(CoachEvidenceDto item, string culture = "en")
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<CoachEvidenceScope>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(CoachEvidenceScope.Item)] = item
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
    /// The rendered text of the withheld line alone, so a copy assertion is about that sentence
    /// rather than about whatever else happens to be on the panel.
    /// </summary>
    private static async Task<string> WithheldLineAsync(CoachWithheldReason reason, string culture)
    {
        var html = await RenderAsync(
            Item(b =>
            {
                b.WithheldCount = 4;
                b.WithheldReason = reason;
            }),
            culture);

        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "<li data-coach-scope=\"withheld\">(?<text>.*?)</li>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            "a positive withheld count must always produce a line; {0}/{1} produced:\n{2}",
            reason,
            culture,
            html);

        return match.Groups["text"].Value;
    }

    private static CoachEvidenceDto Item(Action<Builder>? configure = null)
    {
        var builder = new Builder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private sealed class Builder
    {
        public CoachEvidenceCoverage? Coverage { get; set; }
        public CoachEvidenceOrder? Order { get; set; }
        public CoachDefinitionCode? DefinitionCode { get; set; }
        public CoachWithheldReason? WithheldReason { get; set; }
        public DateTime? AsOfUtc { get; set; }
        public int? MatchedCount { get; set; }
        public int? ReturnedCount { get; set; }
        public int? WithheldCount { get; set; }

        public CoachEvidenceDto Build() => new()
        {
            Kind = CoachEvidenceKind.VocabularyDue,
            Label = "Vocabulary",
            Summary = "Ten words are ready to practise.",
            WindowStartDate = new DateOnly(2026, 8, 1),
            WindowEndDate = new DateOnly(2026, 8, 14),
            Coverage = Coverage,
            Order = Order,
            DefinitionCode = DefinitionCode,
            WithheldReason = WithheldReason,
            AsOfUtc = AsOfUtc,
            MatchedCount = MatchedCount,
            ReturnedCount = ReturnedCount,
            WithheldCount = WithheldCount
        };
    }

    // ── Nothing stated, nothing shown ────────────────────────────────────────

    [Fact]
    public async Task An_item_with_no_scope_renders_no_scope_line()
    {
        var html = await RenderAsync(Item());

        html.Should().NotContain("coach-evidence-scope",
            "an item from a server that states nothing must look exactly as it did before this "
            + "field existed");
    }

    [Fact]
    public async Task Unknown_values_render_as_silence_rather_than_a_guess()
    {
        var html = await RenderAsync(Item(b =>
        {
            b.Coverage = CoachEvidenceCoverage.Unknown;
            b.Order = CoachEvidenceOrder.Unknown;
            b.DefinitionCode = CoachDefinitionCode.Unknown;
            b.WithheldReason = CoachWithheldReason.Unknown;
        }));

        html.Should().NotContain("data-coach-scope=\"coverage\"");
        html.Should().NotContain("data-coach-scope=\"order\"");
        html.Should().NotContain("coach-evidence-scope",
            "a value this build cannot name is a gap, and a gap must look like one");
    }

    [Fact]
    public async Task An_order_that_is_not_a_ranking_makes_no_ordering_claim()
    {
        foreach (var order in new[] { CoachEvidenceOrder.Unordered, CoachEvidenceOrder.NotApplicable })
        {
            var html = await RenderAsync(Item(b => b.Order = order));

            html.Should().NotContain("data-coach-scope=\"order\"",
                "{0} states no ranking, and 'in no particular order' reads as a ranking anyway", order);
        }
    }

    // ── Honest claims ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_page_says_it_is_a_page_and_a_complete_set_says_it_is_complete()
    {
        var page = await RenderAsync(Item(b => b.Coverage = CoachEvidenceCoverage.PageOfOwnedSet));
        page.Should().Contain("A selection, not everything");

        var whole = await RenderAsync(Item(b => b.Coverage = CoachEvidenceCoverage.CompleteOwnedSet));
        whole.Should().Contain("Everything you have");
        whole.Should().NotContain("A selection");
    }

    [Fact]
    public async Task Every_coverage_the_wire_can_carry_has_words_for_it()
    {
        var named = 0;

        foreach (var coverage in Enum.GetValues<CoachEvidenceCoverage>())
        {
            var html = await RenderAsync(Item(b => b.Coverage = coverage));

            if (coverage == CoachEvidenceCoverage.Unknown)
            {
                html.Should().NotContain("data-coach-scope=\"coverage\"");
                continue;
            }

            html.Should().Contain("data-coach-scope=\"coverage\"",
                "{0} is a claim the server can make and the learner must be able to read it", coverage);
            html.Should().NotContain(coverage.ToString(),
                "{0} must render as words, not as its own member name", coverage);
            named++;
        }

        named.Should().Be(8, "eight coverage members carry a claim; the sweep must see all of them");
    }

    [Fact]
    public async Task Every_ranking_order_has_words_for_it()
    {
        var named = 0;

        foreach (var order in Enum.GetValues<CoachEvidenceOrder>())
        {
            var html = await RenderAsync(Item(b => b.Order = order));

            if (order is CoachEvidenceOrder.Unknown
                or CoachEvidenceOrder.Unordered
                or CoachEvidenceOrder.NotApplicable)
            {
                html.Should().NotContain("data-coach-scope=\"order\"", "{0}", order);
                continue;
            }

            html.Should().Contain("data-coach-scope=\"order\"", "{0}", order);
            html.Should().NotContain(order.ToString(), "{0} must render as words", order);
            named++;
        }

        named.Should().Be(7, "seven orders are real rankings; the sweep must see all of them");
    }

    [Fact]
    public async Task The_basis_names_the_sample_and_the_population_when_they_differ()
    {
        var sample = await RenderAsync(Item(b =>
        {
            b.ReturnedCount = 10;
            b.MatchedCount = 14;
        }));

        sample.Should().Contain("Based on 10 of 14",
            "the pair is what tells a total from a sample");

        var whole = await RenderAsync(Item(b =>
        {
            b.ReturnedCount = 10;
            b.MatchedCount = 10;
        }));

        whole.Should().Contain("Based on 10");
        whole.Should().NotContain("of 10", "repeating the same number twice reads as a discrepancy");
    }

    [Fact]
    public async Task The_fourteen_ten_four_disclosure_states_the_count_and_the_reason()
    {
        var html = await RenderAsync(Item(b =>
        {
            b.Coverage = CoachEvidenceCoverage.PageOfOwnedSet;
            b.Order = CoachEvidenceOrder.MasteryDescending;
            b.DefinitionCode = CoachDefinitionCode.UndueVocabularySearch;
            b.WithheldReason = CoachWithheldReason.DueReviewEmbargo;
            b.MatchedCount = 14;
            b.ReturnedCount = 10;
            b.WithheldCount = 4;
        }));

        html.Should().Contain("Based on 10 of 14");
        html.Should().Contain("4 not shown, because they are due for review");
        html.Should().Contain("Strongest first");
        html.Should().Contain("data-coach-definition=\"UndueVocabularySearch\"");
    }

    [Fact]
    public async Task Nothing_withheld_says_nothing()
    {
        var html = await RenderAsync(Item(b =>
        {
            b.WithheldCount = 0;
            b.WithheldReason = CoachWithheldReason.None;
            b.ReturnedCount = 10;
        }));

        html.Should().NotContain("not shown", "a zero is not a disclosure");
    }

    [Fact]
    public async Task A_withheld_count_with_an_unreadable_reason_still_discloses_the_count()
    {
        var html = await RenderAsync(Item(b =>
        {
            b.WithheldCount = 4;
            b.WithheldReason = CoachWithheldReason.Unknown;
        }));

        html.Should().Contain("4 not shown",
            "the number is the disclosure; the explanation is the courtesy, and losing one must "
            + "not lose the other");
        html.Should().NotContain("due for review", "an unreadable reason must not be invented");
    }

    /// <summary>
    /// The exact sentence each withheld reason puts in front of the learner, in both languages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned copy, and pinned deliberately. Two weaker rules were tried first and both let a real
    /// defect through, which is the whole reason this is written out.
    /// </para>
    /// <para>
    /// The first asserted the English word "because". It could only ever hold in one language —
    /// the Korean copy carries no English connective and never will — so it checked half the
    /// surface it claimed to. The second asserted only that a reason's line <em>differs</em> from
    /// the bare count line. That is satisfied by "4 more not shown", which differs by one word and
    /// explains nothing: a learner reading it cannot tell a capped list from four of their own
    /// words held back for review, and those are materially different facts about their account.
    /// </para>
    /// <para>
    /// So the rule is the copy itself. A withheld count is a safety-relevant disclosure — it is how
    /// the coach admits it is holding something back — and changing what it says should require
    /// changing a test that shows the old words next to the new ones. The failure message names the
    /// reason and the culture, so a copy edit reads as a copy edit rather than as a mystery.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_withheld_reason_renders_its_own_words_in_both_languages()
    {
        // Observed from the running component, not composed by hand.
        var expected = new Dictionary<(CoachWithheldReason, string), string>
        {
            // Neutral. Unknown means this build could not read the reason; None paired with a
            // positive count is a server contradiction. Both state the count and invent nothing.
            [(CoachWithheldReason.Unknown, "en")] = "4 not shown",
            [(CoachWithheldReason.Unknown, "ko")] = "4개는 표시하지 않음",
            [(CoachWithheldReason.None, "en")] = "4 not shown",
            [(CoachWithheldReason.None, "ko")] = "4개는 표시하지 않음",

            // Explaining. Each says why, in the learner's own language.
            [(CoachWithheldReason.DueReviewEmbargo, "en")] = "4 not shown, because they are due for review",
            [(CoachWithheldReason.DueReviewEmbargo, "ko")] = "복습할 차례라 4개는 표시하지 않음",
            [(CoachWithheldReason.ResultLimit, "en")] = "4 more not shown, because only the first results are listed",
            [(CoachWithheldReason.ResultLimit, "ko")] = "표시 개수 제한이라 4개는 더 있지만 표시하지 않음",
            [(CoachWithheldReason.ArchivedExcluded, "en")] = "4 not shown, because you archived them",
            [(CoachWithheldReason.ArchivedExcluded, "ko")] = "보관 처리해서 4개는 표시하지 않음",
            [(CoachWithheldReason.BelowMinimumEvidence, "en")] = "4 not shown, because there is not enough practice recorded yet",
            [(CoachWithheldReason.BelowMinimumEvidence, "ko")] = "학습 기록이 부족해 4개는 표시하지 않음"
        };

        expected.Should().HaveCount(12, "six reasons across two languages; the table must not shrink");

        var neutral = new[] { CoachWithheldReason.Unknown, CoachWithheldReason.None };
        var explained = 0;
        var checkedLines = 0;

        foreach (var reason in Enum.GetValues<CoachWithheldReason>())
        {
            foreach (var culture in new[] { "en", "ko" })
            {
                expected.TryGetValue((reason, culture), out var want).Should().BeTrue(
                    "{0}/{1} is a value the server can send and must have copy pinned here",
                    reason,
                    culture);

                var line = await WithheldLineAsync(reason, culture);
                line.Should().Be(want, "{0}/{1}", reason, culture);
                line.Should().NotContain(
                    reason.ToString(), "{0}/{1} must render as words, not a member name", reason, culture);

                checkedLines++;
            }

            var english = expected[(reason, "en")];
            var korean = expected[(reason, "ko")];
            korean.Should().NotBe(english, "{0} must be translated, not passed through", reason);

            if (!neutral.Contains(reason))
            {
                // The assertion the first two rules failed to make: a stated reason must say more
                // than the count-only line, not merely differ from it.
                english.Should().Contain(
                    "because", "{0}'s English copy must explain, not just count", reason);
                korean.Length.Should().BeGreaterThan(
                    expected[(CoachWithheldReason.Unknown, "ko")].Length + 3,
                    "{0}'s Korean copy must carry a reason clause, not a one-word variant", reason);

                explained++;
            }
        }

        checkedLines.Should().Be(12, "the sweep must render every reason in every language");
        explained.Should().Be(
            4,
            "DueReviewEmbargo, ResultLimit, ArchivedExcluded and BelowMinimumEvidence each explain "
            + "a withholding; an earlier census said three and would have passed while one of them "
            + "explained nothing");
    }

    [Fact]
    public async Task A_coherent_none_renders_no_withheld_line_at_all()
    {
        // The only state in which None is not a contradiction. It is why None needs no copy of its
        // own: the line it would carry never renders.
        foreach (var culture in new[] { "en", "ko" })
        {
            var html = await RenderAsync(
                Item(b =>
                {
                    b.WithheldCount = 0;
                    b.WithheldReason = CoachWithheldReason.None;
                }),
                culture);

            html.Should().NotContain("data-coach-scope=\"withheld\"", "{0}", culture);
        }
    }

    // ── The definition code is diagnosis, not prose ──────────────────────────

    [Fact]
    public async Task The_definition_code_is_carried_as_data_and_never_as_learner_prose()
    {
        var named = 0;

        foreach (var code in Enum.GetValues<CoachDefinitionCode>())
        {
            var html = await RenderAsync(Item(b =>
            {
                b.DefinitionCode = code;
                // A visible line, so the container that carries the attribute is rendered at all.
                b.Coverage = CoachEvidenceCoverage.CompleteOwnedSet;
            }));

            var expected = code == CoachDefinitionCode.Unknown ? "unknown" : code.ToString();
            html.Should().Contain($"data-coach-definition=\"{expected}\"", "{0}", code);
            named++;
        }

        named.Should().Be(14, "every definition code must be representable; the sweep must see all of them");
    }

    // ── Localization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_scope_line_is_localized_rather_than_hardcoded()
    {
        var item = Item(b =>
        {
            b.Coverage = CoachEvidenceCoverage.PageOfOwnedSet;
            b.Order = CoachEvidenceOrder.MasteryDescending;
            b.WithheldReason = CoachWithheldReason.DueReviewEmbargo;
            b.MatchedCount = 14;
            b.ReturnedCount = 10;
            b.WithheldCount = 4;
        });

        var english = await RenderAsync(item, "en");
        var korean = await RenderAsync(item, "ko");

        english.Should().Contain("A selection, not everything");
        english.Should().Contain("Strongest first");

        korean.Should().Contain("전체가 아닌 일부");
        korean.Should().Contain("숙달도 높은 순");
        korean.Should().Contain("14개 중 10개 기준");
        korean.Should().Contain("복습할 차례라 4개는 표시하지 않음");

        korean.Should().NotContain("A selection, not everything",
            "a Korean-display learner must not be handed English copy");
        korean.Should().NotContain("Based on");
        korean.Should().NotContain("not shown");
    }

    [Fact]
    public async Task The_as_of_stamp_renders_in_the_cultures_own_pattern()
    {
        var item = Item(b => b.AsOfUtc = new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc));

        var english = await RenderAsync(item, "en");
        var korean = await RenderAsync(item, "ko");

        english.Should().Contain("As of ");
        korean.Should().Contain(" 기준");

        korean.Should().NotContain("As of ",
            "the as-of stamp is learner-facing copy and must follow the display language");

        foreach (var html in new[] { english, korean })
        {
            html.Should().NotContain("22:14:07",
                "the wire value is UTC; the learner reads their own clock");
            html.Should().NotContain(".000", "the instant is whole-second and must render as one");
        }
    }
}
