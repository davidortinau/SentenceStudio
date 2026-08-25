using FluentAssertions;
using FluentAssertions.Execution;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The order rule against every ranking the wire can state, in both display languages.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the widening changed, and what it must not.</b> The rule used to exit whenever any
/// evidence item stated a real order, so it could only catch prose ranking an admittedly unranked
/// set — the opposite of the case worth catching. It now compares the claimed measure and direction
/// against the recorded one. The risk that creates is the mirror image: a rule that fires on any
/// order word near any evidence would punish an answer that correctly describes its own read, which
/// is the worse failure because it trains the model out of describing rankings at all.
/// </para>
/// <para>
/// So this file is a matrix rather than a handful of cases. For every member of
/// <see cref="CoachEvidenceOrder"/> that states a ranking there is a claim that matches it and must
/// stay silent, and a claim that contradicts it and must fire. The census at the end asserts the
/// matrix covers the enum, so a member added later is not quietly untested.
/// </para>
/// </remarks>
public sealed class CoachOrderClaimMatrixTests
{
    private static readonly CoachEvidenceOrder[] RankingOrders =
    [
        CoachEvidenceOrder.LastUsedAscending,
        CoachEvidenceOrder.UpdatedDescending,
        CoachEvidenceOrder.MasteryDescending,
        CoachEvidenceOrder.MinutesDescending,
        CoachEvidenceOrder.PriorityAscending,
        CoachEvidenceOrder.FrequencyDescending,
        CoachEvidenceOrder.BandLabelAscending
    ];

    private static readonly CoachEvidenceOrder[] NonRankingOrders =
    [
        CoachEvidenceOrder.Unknown,
        CoachEvidenceOrder.NotApplicable,
        CoachEvidenceOrder.Unordered
    ];

    private static CoachClaimFinding[] Scan(string text, CoachEvidenceOrder? order) =>
        [.. new CoachOrderClaimMismatchRule().Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(text),
            Evidence = [ClaimFixture.Evidence(order: order)]
        })];

    // ═════════════════════════════════════════════════════════════════════════
    // Matches — the answer describes the ranking the read used
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>An answer that names the recorded ranking is not a mismatch.</summary>
    /// <remarks>
    /// The fence on the widening, one row per stated order. Every sentence here is true of its
    /// evidence, and a build that fired on any of them would refuse correct answers.
    /// </remarks>
    [Theory]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "Here are the words you know best.")]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "Sorted by mastery.")]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "Your strongest words first.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "Your newest words first.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "The most recently updated ones.")]
    [InlineData(CoachEvidenceOrder.LastUsedAscending, "The ones you used most recently.")]
    [InlineData(CoachEvidenceOrder.LastUsedAscending, "Sorted by how recently you practised.")]
    [InlineData(CoachEvidenceOrder.MinutesDescending, "Where you spent the most time.")]
    [InlineData(CoachEvidenceOrder.MinutesDescending, "Sorted by minutes.")]
    [InlineData(CoachEvidenceOrder.FrequencyDescending, "The ones that come up most often.")]
    [InlineData(CoachEvidenceOrder.FrequencyDescending, "Your most common words.")]
    [InlineData(CoachEvidenceOrder.PriorityAscending, "Your highest priority items.")]
    [InlineData(CoachEvidenceOrder.PriorityAscending, "The most important ones first.")]
    public void A_claim_that_names_the_recorded_ranking_is_silent(CoachEvidenceOrder order, string text) =>
        Scan(text, order).Should().BeEmpty(
            "the answer describes the order the read actually used");

    // ═════════════════════════════════════════════════════════════════════════
    // Mismatches — the answer names a different ranking, or the other end of it
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>An answer that names a different measure contradicts the recorded ranking.</summary>
    /// <remarks>
    /// The Case C shape generalised. Each row is a specific, confident sentence about the one thing
    /// a learner uses to decide what to study first, and each is wrong about it.
    /// </remarks>
    [Theory]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "Your newest words first.")]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "Sorted by minutes.")]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "Your highest priority items.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "Here are the words you know best.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "The ones that come up most often.")]
    [InlineData(CoachEvidenceOrder.LastUsedAscending, "Sorted by mastery.")]
    [InlineData(CoachEvidenceOrder.MinutesDescending, "Your newest words first.")]
    [InlineData(CoachEvidenceOrder.MinutesDescending, "Your strongest words first.")]
    [InlineData(CoachEvidenceOrder.FrequencyDescending, "Where you spent the most time.")]
    [InlineData(CoachEvidenceOrder.PriorityAscending, "Your oldest words first.")]
    [InlineData(CoachEvidenceOrder.BandLabelAscending, "Here are the words you know best.")]
    [InlineData(CoachEvidenceOrder.BandLabelAscending, "Your newest words first.")]
    public void A_claim_that_names_a_different_measure_fires(CoachEvidenceOrder order, string text) =>
        Scan(text, order).Should().ContainSingle(
            "the recorded order is not the one the answer describes")
            .Which.Rule.Should().Be(CoachClaimRuleCode.OrderClaimMismatch);

    /// <summary>The right measure pointed the wrong way is still a mismatch.</summary>
    /// <remarks>
    /// The subtler half, and the one a measure-only comparison would miss. A read sorted strongest
    /// first, described as "your weakest words", puts the learner's attention on exactly the rows
    /// they need it least.
    /// </remarks>
    [Theory]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "Your weakest words first.")]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "The ones you know least well.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "Your oldest words first.")]
    [InlineData(CoachEvidenceOrder.LastUsedAscending, "The ones you used least recently.")]
    [InlineData(CoachEvidenceOrder.MinutesDescending, "Where you spent the least time.")]
    [InlineData(CoachEvidenceOrder.FrequencyDescending, "Your least common words.")]
    [InlineData(CoachEvidenceOrder.PriorityAscending, "Your lowest priority items.")]
    public void The_right_measure_in_the_wrong_direction_fires(CoachEvidenceOrder order, string text) =>
        Scan(text, order).Should().ContainSingle(
            "the direction is the claim; reversing it reverses what the learner should study")
            .Which.Rule.Should().Be(CoachClaimRuleCode.OrderClaimMismatch);

    /// <summary>Prose asserting there is no ranking, over a read that recorded one.</summary>
    [Theory]
    [InlineData(CoachEvidenceOrder.MasteryDescending)]
    [InlineData(CoachEvidenceOrder.MinutesDescending)]
    public void Claiming_no_order_over_a_recorded_ranking_fires(CoachEvidenceOrder order) =>
        Scan("These are in no particular order.", order).Should().ContainSingle(
            "the read ranked them, and telling the learner otherwise discards information they "
            + "would have used")
            .Which.Rule.Should().Be(CoachClaimRuleCode.OrderClaimMismatch);

    // ═════════════════════════════════════════════════════════════════════════
    // Preserved behaviour — prose ranking a read that declared nothing
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An explicit ranking claim over an unranked read still fires, for every non-ranking order.
    /// </summary>
    /// <remarks>
    /// The case the rule was originally written for. Over a read that declared no ranking, a bare
    /// superlative is unsupported whatever it is ranking by, so the measure never has to resolve —
    /// which is why this path keeps the broad marker set.
    /// </remarks>
    [Theory]
    [InlineData(CoachEvidenceOrder.Unordered)]
    [InlineData(CoachEvidenceOrder.Unknown)]
    [InlineData(CoachEvidenceOrder.NotApplicable)]
    [InlineData(null)]
    public void An_explicit_ranking_over_an_unranked_read_still_fires(CoachEvidenceOrder? order)
    {
        using var _ = new AssertionScope();

        Scan("Your most-practised resource is the news reader.", order).Should().ContainSingle(
            "an unresolvable measure is still unsupported when the read declared no ranking at all");

        Scan("Your newest words first.", order).Should().ContainSingle();
        Scan("Here are the words you know best.", order).Should().ContainSingle();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Silence — no claim, ambiguity, and teaching material
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Ordinary prose that mentions recency makes no ordering claim.</summary>
    /// <remarks>
    /// The false positive this rule is most exposed to. "Recently" is a common word and only a
    /// superlative or an explicit sort phrase turns it into a ranking, so every sentence here has to
    /// stay silent even over a read whose order it would otherwise contradict.
    /// </remarks>
    [Theory]
    [InlineData("You practised these recently.")]
    [InlineData("This word was added recently.")]
    [InlineData("Recently you have been working on verbs.")]
    [InlineData("A new word appeared in your last session.")]
    [InlineData("Here are four of your words.")]
    [InlineData("You have ten words due for review.")]
    public void Prose_that_makes_no_ordering_claim_is_silent(string text)
    {
        using var _ = new AssertionScope();

        foreach (var order in RankingOrders)
        {
            Scan(text, order).Should().BeEmpty(
                "'{0}' asserts no ranking, so there is nothing for {1} to contradict", text, order);
        }
    }

    /// <summary>An unresolvable rank word over a stated ranking stays silent.</summary>
    /// <remarks>
    /// "Most-practised" could mean the most minutes or the most sessions, and the two map to
    /// different orders. Guessing would fire on an answer that was true under the other reading, so
    /// the parser reports ambiguity and the rule declines. The pair below is what keeps that from
    /// being read as "the rule ignores this sentence": over an unranked read the same sentence still
    /// fires.
    /// </remarks>
    [Fact]
    public void An_ambiguous_rank_word_over_a_stated_ranking_is_silent()
    {
        using var _ = new AssertionScope();

        Scan("Your most-practised resource is the news reader.", CoachEvidenceOrder.MinutesDescending)
            .Should().BeEmpty("the measure is genuinely ambiguous and a guess would punish a true answer");

        Scan("Your most-practised resource is the news reader.", CoachEvidenceOrder.Unordered)
            .Should().ContainSingle("but the same sentence over an unranked read is still unsupported");
    }

    /// <summary>Teaching blocks are out of scope whatever they say.</summary>
    /// <remarks>
    /// An <c>Example</c> sentence containing "the newest word" is teaching material, not a claim
    /// about this learner's read. Scope excludes it by block kind, and this pins that the widening
    /// did not reach around the scope.
    /// </remarks>
    [Theory]
    [InlineData(CoachAnswerBlockKind.Example)]
    [InlineData(CoachAnswerBlockKind.Form)]
    [InlineData(CoachAnswerBlockKind.Contrast)]
    public void Teaching_blocks_are_never_scanned_for_ordering_claims(CoachAnswerBlockKind kind)
    {
        var findings = new CoachOrderClaimMismatchRule().Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Your newest words first.", kind),
            Evidence = [ClaimFixture.Evidence(order: CoachEvidenceOrder.MasteryDescending)]
        });

        findings.Should().BeEmpty("a worked example is not a claim about the learner's data");
    }

    /// <summary>A target-language span is never scanned.</summary>
    [Fact]
    public void Target_language_spans_are_never_scanned()
    {
        var findings = new CoachOrderClaimMismatchRule().Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(
                "가장 최근에 추가한 단어입니다.",
                CoachAnswerBlockKind.Answer,
                CoachLanguageRole.Target),
            Evidence = [ClaimFixture.Evidence(order: CoachEvidenceOrder.MasteryDescending)]
        });

        findings.Should().BeEmpty("the text the learner is here to read is not a claim about them");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Korean display copy
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>The Korean equivalents contradict the same orders their English twins do.</summary>
    /// <remarks>
    /// A Korean-display learner reads Korean copy, and a rule that only understood English would
    /// have a language-shaped blind spot in exactly the deployment the language carve-out was
    /// written for. Bounded markers, not a parser.
    /// </remarks>
    [Theory]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "가장 최근에 추가한 단어입니다.")]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "가장 오래된 단어부터입니다.")]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "가장 자주 나오는 단어입니다.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "가장 잘 아는 단어입니다.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "우선순위가 가장 높은 항목입니다.")]
    [InlineData(CoachEvidenceOrder.MinutesDescending, "가장 최근에 추가한 단어입니다.")]
    [InlineData(CoachEvidenceOrder.FrequencyDescending, "가장 잘 아는 단어입니다.")]
    public void Korean_claims_contradict_the_same_orders(CoachEvidenceOrder order, string text) =>
        Scan(text, order).Should().ContainSingle(
            "the Korean copy states the same ranking its English twin does")
            .Which.Rule.Should().Be(CoachClaimRuleCode.OrderClaimMismatch);

    /// <summary>And the matching Korean claim stays silent.</summary>
    [Theory]
    [InlineData(CoachEvidenceOrder.MasteryDescending, "가장 잘 아는 단어입니다.")]
    [InlineData(CoachEvidenceOrder.UpdatedDescending, "가장 최근에 추가한 단어입니다.")]
    [InlineData(CoachEvidenceOrder.FrequencyDescending, "가장 자주 나오는 단어입니다.")]
    [InlineData(CoachEvidenceOrder.PriorityAscending, "우선순위가 가장 높은 항목입니다.")]
    [InlineData(CoachEvidenceOrder.MinutesDescending, "가장 오래 연습한 자료입니다.")]
    public void Korean_claims_that_match_the_recorded_ranking_are_silent(
        CoachEvidenceOrder order, string text) =>
        Scan(text, order).Should().BeEmpty("the Korean copy describes the order the read used");

    // ═════════════════════════════════════════════════════════════════════════
    // The metric and the report read the rule code, and count it once
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The Case C shape produces exactly one order finding, and it reaches the durable summary once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The metric and the report column are both derived from the rule code by
    /// <c>CoachGroundingTurnProjection</c>, so nothing had to be registered for the widened rule to
    /// appear in them. What does need checking is the count: the answer has two spans and only one
    /// makes an ordering claim, so a rule that fired per span — or per evidence item — would inflate
    /// the soak numerator on exactly the shape the gate reads.
    /// </para>
    /// <para>
    /// Asserted through the real projection rather than by counting findings, because the projection
    /// is what the counter and the stored row are built from.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_case_c_shape_counts_one_order_mismatch_in_the_durable_summary()
    {
        var findings = new CoachOrderClaimMismatchRule().Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.AnswerWith(
                "Your newest words, sorted by when you added them.",
                "You have 30 words tracked right now."),
            Evidence = [ClaimFixture.Evidence(
                order: CoachEvidenceOrder.MasteryDescending, matched: 14, returned: 4, withheld: 10)]
        }).ToArray();

        findings.Should().ContainSingle("one span claims an order; the other states a count");

        var summary = CoachGroundingTurnProjection.Project(new CoachClaimTurnRecord(
            CoachGroundingStage.Enforce,
            findings,
            Refused: false,
            AnswerAltered: false,
            CoachShadowRouteLabel.Unknown,
            Limitation: null));

        summary!.RuleCounts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new CoachGroundingRuleCount(CoachClaimRuleCode.OrderClaimMismatch, 1),
                "the counter and the report column both read this, and a per-span count would "
                + "inflate the soak numerator on the exact shape the gate reads");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Census
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>The matrix covers every order the wire can state.</summary>
    /// <remarks>
    /// Derived from the enum, so a member added later fails here rather than being silently
    /// untested — which is how the original gap survived: the rule handled one order class and the
    /// tests only ever asked it about that class.
    /// </remarks>
    [Fact]
    public void The_matrix_covers_every_declared_evidence_order()
    {
        var declared = Enum.GetValues<CoachEvidenceOrder>().ToHashSet();
        var covered = RankingOrders.Concat(NonRankingOrders).ToHashSet();

        covered.Should().BeEquivalentTo(
            declared,
            "an order the matrix does not exercise is an order the rule's behaviour over it is "
            + "unknown for");

        RankingOrders.Should().HaveCountGreaterThan(
            5, "the ranking half must be the bulk of it, not one example");
    }

    /// <summary>Every ranking order has both a matching and a contradicting fixture.</summary>
    /// <remarks>
    /// Non-vacuity for the two theories above: a matrix of only-fires or only-silent rows would
    /// pass against a rule that always did one or the other.
    /// </remarks>
    [Fact]
    public void Every_ranking_order_is_exercised_in_both_directions()
    {
        using var _ = new AssertionScope();

        foreach (var order in RankingOrders)
        {
            var contradicting = order == CoachEvidenceOrder.BandLabelAscending
                ? "Here are the words you know best."
                : Contradiction(order);

            Scan(contradicting, order).Should().ContainSingle(
                "{0} must have a claim that contradicts it", order);

            if (order == CoachEvidenceOrder.BandLabelAscending)
            {
                // No learner-measure claim matches a label ordering, by the enum's own definition.
                continue;
            }

            Scan(Agreement(order), order).Should().BeEmpty(
                "{0} must have a claim that agrees with it", order);
        }
    }

    private static string Agreement(CoachEvidenceOrder order) => order switch
    {
        CoachEvidenceOrder.MasteryDescending => "Here are the words you know best.",
        CoachEvidenceOrder.UpdatedDescending => "Your newest words first.",
        CoachEvidenceOrder.LastUsedAscending => "The ones you used most recently.",
        CoachEvidenceOrder.MinutesDescending => "Where you spent the most time.",
        CoachEvidenceOrder.FrequencyDescending => "The ones that come up most often.",
        CoachEvidenceOrder.PriorityAscending => "Your highest priority items.",
        _ => throw new ArgumentOutOfRangeException(nameof(order), order, "no agreement fixture")
    };

    private static string Contradiction(CoachEvidenceOrder order) => order switch
    {
        CoachEvidenceOrder.MasteryDescending => "Your newest words first.",
        CoachEvidenceOrder.UpdatedDescending => "Here are the words you know best.",
        CoachEvidenceOrder.LastUsedAscending => "Sorted by mastery.",
        CoachEvidenceOrder.MinutesDescending => "Your strongest words first.",
        CoachEvidenceOrder.FrequencyDescending => "Where you spent the most time.",
        CoachEvidenceOrder.PriorityAscending => "Your oldest words first.",
        _ => throw new ArgumentOutOfRangeException(nameof(order), order, "no contradiction fixture")
    };
}
