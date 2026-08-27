using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The nine honesty rules, each with a fixture that fires it and one that must not.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here has both halves, and the negative half is the one that matters. A rule that
/// fires on everything satisfies its positive test perfectly and destroys the product: the coach
/// stops teaching, because "Korean has 7 speech levels" trips a count rule and "you can practise
/// this pattern" trips a state rule. The plan says the same thing in B6 — this is not a digit ban —
/// and a suite without negative fixtures cannot tell the difference.
/// </para>
/// <para>
/// Two properties recur and are worth naming once. <b>No trace means no finding</b> for the two
/// rules that audit whether a read happened: an unrecorded turn is unproven, not guilty, and the
/// alternative would have made every stored pre-W4 turn a violation on the day this shipped.
/// <b>Findings carry no text</b>, so an assertion can name a rule and a location and never repeat
/// the sentence that caused it.
/// </para>
/// </remarks>
public sealed class CoachClaimRuleTests
{
    // ── UnverifiedLearnerStateClaim ──────────────────────────────────────────

    [Fact]
    public void Unverified_state_fires_when_a_learner_claim_has_no_read_behind_it()
    {
        var rule = new CoachUnverifiedLearnerStateClaimRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have been practising verbs a lot lately."),
            Trace = ClaimFixture.EmptyTrace()
        }).ToArray();

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(CoachClaimRuleCode.UnverifiedLearnerStateClaim);
    }

    [Fact]
    public void Unverified_state_is_silent_when_a_read_produced_evidence()
    {
        var rule = new CoachUnverifiedLearnerStateClaimRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have been practising verbs a lot lately."),
            Evidence = [ClaimFixture.Evidence()],
            Trace = ClaimFixture.SuccessfulTrace()
        });

        findings.Should().BeEmpty("the claim is backed by a read that succeeded and returned evidence");
    }

    /// <summary>B6, stated as a test. Teaching survives.</summary>
    [Theory]
    [InlineData("Korean has 7 speech levels, and 3 of them are common.")]
    [InlineData("The verb takes 2 forms depending on politeness.")]
    [InlineData("You can practise this pattern with any noun.")]
    [InlineData("You should try reading it aloud.")]
    [InlineData("This is how the form is built.")]
    public void Unverified_state_does_not_fire_on_instruction(string text)
    {
        var rule = new CoachUnverifiedLearnerStateClaimRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(text),
            Trace = ClaimFixture.EmptyTrace()
        });

        findings.Should().BeEmpty(
            "B6 says the rule is scoped by referent, not a digit ban. A rule that suppressed facts "
            + "about the language would make the coach useless at the thing it is for");
    }

    [Fact]
    public void Unverified_state_is_silent_with_no_trace_at_all()
    {
        var rule = new CoachUnverifiedLearnerStateClaimRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have thirty words due."),
            Trace = null
        });

        findings.Should().BeEmpty(
            "an unrecorded turn is unproven, not guilty; treating a null trace as a violation would "
            + "have condemned every version-1 stored turn the day this shipped");
    }

    // ── NegativeClaimWithoutCoverage ─────────────────────────────────────────

    [Fact]
    public void Negative_claim_fires_over_a_page()
    {
        var rule = new CoachNegativeClaimWithoutCoverageRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You don't have any verbs to review."),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.PageOfOwnedSet)]
        }).ToArray();

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(CoachClaimRuleCode.NegativeClaimWithoutCoverage);
    }

    [Fact]
    public void Negative_claim_is_silent_over_a_complete_set()
    {
        var rule = new CoachNegativeClaimWithoutCoverageRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You don't have any verbs to review."),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.CompleteOwnedSet)]
        });

        findings.Should().BeEmpty("a complete set is the only thing that can support an absolute");
    }

    /// <summary>A counted zero is a count, and the count rule owns it.</summary>
    [Fact]
    public void Negative_claim_does_not_fire_on_a_counted_statement()
    {
        var rule = new CoachNegativeClaimWithoutCoverageRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have 0 verbs due in the last 7 days."),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.PageOfOwnedSet)]
        });

        findings.Should().BeEmpty(
            "the no-digit condition is what separates a bounded count from an unbounded absolute; "
            + "two rules firing on one span would double-count the metric");
    }

    // ── FabricatedCheck ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("I checked your vocabulary and it looks healthy.")]
    [InlineData("Let me look at your practice history.")]
    [InlineData("After reviewing your log, here is what stands out.")]
    public void Fabricated_check_fires_when_nothing_succeeded(string text)
    {
        var rule = new CoachFabricatedCheckRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(text),
            Trace = ClaimFixture.FailedTrace()
        }).ToArray();

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(CoachClaimRuleCode.FabricatedCheck);
    }

    [Fact]
    public void Fabricated_check_is_silent_when_a_read_succeeded()
    {
        var rule = new CoachFabricatedCheckRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I checked your vocabulary and it looks healthy."),
            Trace = ClaimFixture.SuccessfulTrace()
        });

        findings.Should().BeEmpty();
    }

    /// <summary>Hedging is honest. Only an assertion that a read occurred is a fabricated check.</summary>
    [Theory]
    [InlineData("It looks like this pattern is common.")]
    [InlineData("Here is how the form works.")]
    public void Fabricated_check_does_not_fire_on_hedging_or_teaching(string text)
    {
        var rule = new CoachFabricatedCheckRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(text),
            Trace = ClaimFixture.EmptyTrace()
        });

        findings.Should().BeEmpty();
    }

    // ── OrderClaimMismatch ───────────────────────────────────────────────────

    [Fact]
    public void Order_mismatch_fires_on_a_superlative_over_an_unordered_read()
    {
        var rule = new CoachOrderClaimMismatchRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Your most-practised resource is the news reader."),
            Evidence = [ClaimFixture.Evidence(order: CoachEvidenceOrder.Unordered)]
        }).ToArray();

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(CoachClaimRuleCode.OrderClaimMismatch);
    }

    [Fact]
    public void Order_mismatch_is_silent_when_the_evidence_states_a_ranking()
    {
        var rule = new CoachOrderClaimMismatchRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Your most-practised resource is the news reader."),
            Evidence = [ClaimFixture.Evidence(order: CoachEvidenceOrder.MinutesDescending)]
        });

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Order_mismatch_is_silent_with_no_evidence()
    {
        var rule = new CoachOrderClaimMismatchRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Your most-practised resource is the news reader.")
        });

        findings.Should().BeEmpty(
            "with no evidence there is no order to contradict; that turn belongs to the "
            + "unverified-state rule, and two findings for one defect inflate the metric");
    }

    // ── CountClaimMismatch ───────────────────────────────────────────────────

    [Fact]
    public void Count_mismatch_fires_on_a_number_the_evidence_never_produced()
    {
        var rule = new CoachCountClaimMismatchRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have 42 words due this week."),
            Evidence = [ClaimFixture.Evidence(matched: 84, returned: 20)]
        }).ToArray();

        var finding = findings.Should().ContainSingle().Subject;
        finding.Rule.Should().Be(CoachClaimRuleCode.CountClaimMismatch);
        finding.ClaimedCount.Should().Be(42);
        finding.EvidenceCount.Should().Be(84);
    }

    [Theory]
    [InlineData(84)]
    [InlineData(20)]
    public void Count_mismatch_is_silent_on_a_supported_number(int stated)
    {
        var rule = new CoachCountClaimMismatchRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer($"You have {stated} words in that set."),
            Evidence = [ClaimFixture.Evidence(matched: 84, returned: 20)]
        });

        findings.Should().BeEmpty(
            "both halves of '20 of your 84' are supported; rewriting a true sentence is worse than "
            + "the defect this rule exists for");
    }

    /// <summary>The B6 line again, on the rule most tempted to become a digit ban.</summary>
    [Fact]
    public void Count_mismatch_does_not_fire_on_a_fact_about_the_language()
    {
        var rule = new CoachCountClaimMismatchRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Korean has 7 speech levels."),
            Evidence = [ClaimFixture.Evidence(matched: 84)]
        });

        findings.Should().BeEmpty("no second-person referent, so it is not a claim about the learner");
    }

    [Fact]
    public void Count_mismatch_ignores_small_quantifiers()
    {
        var rule = new CoachCountClaimMismatchRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have 2 of these in your list."),
            Evidence = [ClaimFixture.Evidence(matched: 84)]
        });

        findings.Should().BeEmpty(
            "one through three read as ordinary quantifiers, and matching them would bury the real "
            + "findings under noise");
    }

    // ── WithheldNotDisclosed ─────────────────────────────────────────────────

    [Fact]
    public void Withheld_fires_when_rows_were_held_back_silently()
    {
        var rule = new CoachWithheldNotDisclosedRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Here are your words."),
            Evidence = [ClaimFixture.Evidence(withheld: 5)]
        }).ToArray();

        var finding = findings.Should().ContainSingle().Subject;
        finding.Rule.Should().Be(CoachClaimRuleCode.WithheldNotDisclosed);
        finding.EvidenceCount.Should().Be(5);
        finding.BlockIndex.Should().BeNull("an absence has no coordinates");
    }

    /// <summary>
    /// Disclosure is the structured evidence pair, in any language.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced a test that asserted English prose silenced the rule. That was the bug: the
    /// old rule matched words like "not shown", so a Korean answer that disclosed the withholding
    /// perfectly matched nothing and the rule fired on a coach that had done the right thing — and
    /// at Enforce it refused the turn for it.
    /// </para>
    /// <para>
    /// Both cases below carry the same evidence and differ only in the language of the prose. The
    /// rule must be silent for both, because the disclosure is the count and the reason the panel
    /// renders, not the sentence beside it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Some are not shown because they are due for review.")]
    // 복습할 단어라서 일부는 표시되지 않았어요. — the same disclosure, in Korean.
    [InlineData("\uBCF5\uC2B5\uD560 \uB2E8\uC5B4\uB77C\uC11C \uC77C\uBD80\uB294 \uD45C\uC2DC\uB418\uC9C0 \uC54A\uC558\uC5B4\uC694.")]
    // No prose at all. The panel still says it, so the answer does not have to.
    [InlineData("Here is what I found.")]
    public void Withheld_is_silent_when_the_evidence_discloses_it(string secondSpan)
    {
        var rule = new CoachWithheldNotDisclosedRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.AnswerWith("Here are your words.", secondSpan),
            Evidence =
            [
                ClaimFixture.Evidence(withheld: 5, withheldReason: CoachWithheldReason.DueReviewEmbargo)
            ]
        });

        findings.Should().BeEmpty(
            "a visible count with a known reason is disclosure the client renders in the learner's "
            + "own language; requiring English prose made honesty a property of the display language");
    }

    /// <summary>An incoherent pair discloses nothing, however the answer is worded.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(CoachWithheldReason.None)]
    [InlineData(CoachWithheldReason.Unknown)]
    public void Withheld_still_fires_when_the_reason_is_missing(CoachWithheldReason? reason)
    {
        var rule = new CoachWithheldNotDisclosedRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            // Prose that would have satisfied the old English regex. It is not disclosure, because
            // the panel beside it cannot say why anything was held back.
            Answer = ClaimFixture.AnswerWith(
                "Here are your words.",
                "Some are not shown."),
            Evidence = [ClaimFixture.Evidence(withheld: 5, withheldReason: reason)]
        });

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(
                CoachClaimRuleCode.WithheldNotDisclosed,
                "a count the panel cannot explain leaves the finding standing and unrepairable");
    }

    [Fact]
    public void Withheld_is_silent_when_nothing_was_held_back()
    {
        var rule = new CoachWithheldNotDisclosedRule();

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Here are your words."),
            Evidence = [ClaimFixture.Evidence(withheld: 0)]
        });

        findings.Should().BeEmpty();
    }

    // ── CapabilityAbsent. AC-F2. ─────────────────────────────────────────────

    [Theory]
    [InlineData(CoachCapabilityAvailability.PresentOnAnotherSurface)]
    [InlineData(CoachCapabilityAvailability.AbsentByDesign)]
    [InlineData(CoachCapabilityAvailability.AbsentUnimplemented)]
    [InlineData(CoachCapabilityAvailability.Unknown)]
    public void Capability_absent_fires_on_anything_short_of_present(
        CoachCapabilityAvailability availability)
    {
        var resolver = new StubCapabilityResolver().Declare("set_theme", availability);
        var rule = new CoachCapabilityAbsentRule(resolver);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I'll switch you to the dark theme."),
            ProposedCapabilities = ["set_theme"]
        }).ToArray();

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(CoachClaimRuleCode.CapabilityAbsent);
    }

    /// <summary>AC-F1: manifest declares it, stage is met, synthetic handshake advertises it.</summary>
    [Fact]
    public void Capability_absent_is_silent_when_the_manifest_resolves_present()
    {
        var resolver = new StubCapabilityResolver()
            .Declare("set_theme", CoachCapabilityAvailability.Present);

        var rule = new CoachCapabilityAbsentRule(resolver);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I'll switch you to the dark theme."),
            ProposedCapabilities = ["set_theme"]
        });

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Capability_absent_is_silent_when_nothing_was_proposed()
    {
        var rule = new CoachCapabilityAbsentRule(new StubCapabilityResolver());

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Here is how that grammar pattern works.")
        });

        findings.Should().BeEmpty("a teaching turn proposes nothing and cannot over-claim");
    }

    // ── FalseLimitation. AC-F3. ──────────────────────────────────────────────

    [Theory]
    [InlineData("I can't change your theme.")]
    [InlineData("I cannot do that for you.")]
    [InlineData("That's not something I can do.")]
    [InlineData("Changing the theme is not supported.")]
    public void False_limitation_fires_when_the_capability_is_present(string text)
    {
        var resolver = new StubCapabilityResolver()
            .Declare("set_theme", CoachCapabilityAvailability.Present);

        var rule = new CoachFalseLimitationRule(resolver);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(text),
            ProposedCapabilities = ["set_theme"]
        }).ToArray();

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(CoachClaimRuleCode.FalseLimitation);
    }

    /// <summary>"On another screen" is still capable. A flat no is still wrong.</summary>
    [Fact]
    public void False_limitation_fires_when_the_capability_lives_on_another_surface()
    {
        var resolver = new StubCapabilityResolver()
            .Declare("set_theme", CoachCapabilityAvailability.PresentOnAnotherSurface);

        var rule = new CoachFalseLimitationRule(resolver);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I can't change your theme."),
            ProposedCapabilities = ["set_theme"]
        });

        findings.Should().ContainSingle(
            "§5.6 says it never produces a flat refusal; the honest answer is the screen");
    }

    [Fact]
    public void False_limitation_is_silent_when_the_capability_really_is_absent()
    {
        var resolver = new StubCapabilityResolver()
            .Declare("set_theme", CoachCapabilityAvailability.AbsentUnimplemented);

        var rule = new CoachFalseLimitationRule(resolver);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I can't change your theme."),
            ProposedCapabilities = ["set_theme"]
        });

        findings.Should().BeEmpty("a true limitation is the correct answer, not a violation");
    }

    /// <summary>The W7 boundary must survive. A correct refusal is not a false limitation.</summary>
    [Fact]
    public void False_limitation_does_not_fire_on_a_boundary_refusal()
    {
        var rule = new CoachFalseLimitationRule(new StubCapabilityResolver());

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I can't give you today's review answers."),
            ProposedCapabilities = []
        });

        findings.Should().BeEmpty(
            "matching inability language alone would fire on every S16 answer, which is a correct "
            + "refusal and a designed boundary");
    }

    // ── SideEffectNotDisclosed. AC-G2. ───────────────────────────────────────

    [Fact]
    public void Side_effect_fires_when_a_writing_capability_is_proposed_silently()
    {
        var manifest = new StubCapabilityManifest()
            .Declare("apply_plan_change", CoachCapabilityEffectClass.LearnerData);

        var rule = new CoachSideEffectNotDisclosedRule(manifest);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Sounds good."),
            ProposedCapabilities = ["apply_plan_change"]
        }).ToArray();

        findings.Should().ContainSingle()
            .Which.Rule.Should().Be(CoachClaimRuleCode.SideEffectNotDisclosed);
    }

    [Fact]
    public void Side_effect_is_silent_when_the_answer_states_the_consequence()
    {
        var manifest = new StubCapabilityManifest()
            .Declare("apply_plan_change", CoachCapabilityEffectClass.LearnerData);

        var rule = new CoachSideEffectNotDisclosedRule(manifest);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("This will change today's plan, and you can undo it."),
            ProposedCapabilities = ["apply_plan_change"]
        });

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Side_effect_is_silent_for_a_read()
    {
        var manifest = new StubCapabilityManifest()
            .Declare("get_vocabulary_due_summary", CoachCapabilityEffectClass.Read);

        var rule = new CoachSideEffectNotDisclosedRule(manifest);

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Here is your vocabulary summary."),
            ProposedCapabilities = ["get_vocabulary_due_summary"]
        });

        findings.Should().BeEmpty(
            "a read changes nothing, and a disclosure sentence beside every read teaches both the "
            + "model and the reader to ignore the disclosure that matters");
    }

    [Fact]
    public void Side_effect_is_silent_for_a_capability_the_manifest_does_not_know()
    {
        var rule = new CoachSideEffectNotDisclosedRule(new StubCapabilityManifest());

        var findings = rule.Evaluate(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Sounds good."),
            ProposedCapabilities = ["some_future_thing"]
        });

        findings.Should().BeEmpty(
            "an undeclared capability has no declared effect to disclose; CapabilityAbsent owns "
            + "that turn");
    }
}
