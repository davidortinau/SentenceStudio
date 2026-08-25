using SentenceStudio.Api.Coach.Evidence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Evidence;

/// <summary>
/// Evidence is projected from reads that happened, and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The defect this closes had no symptom a reviewer could see. Evidence was built from
/// <c>intent.EvidenceReferences</c> — the model's own claim — so the card a learner opened to check
/// whether a claim was real was itself generated from the claim. A turn that consulted nothing and
/// asserted "PracticeBalance, 7 days" rendered a card identical to one backed by a query. Every
/// test below exists because the old code passed all of them vacuously: it could not fail them,
/// because it never consulted the record of what was read.
/// </para>
/// </remarks>
public class CoachTurnEvidenceProjectionTests
{
    private static readonly DateOnly Today = new(2026, 8, 21);
    private static readonly DateTime Now = new(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc);

    // =====================================================================
    // Projection is grounded in real scopes
    // =====================================================================

    [Fact]
    public void A_turn_that_read_nothing_produces_no_evidence()
    {
        CoachTurnEvidenceProjection.Project([], Today).Should().BeEmpty();
        CoachTurnEvidenceProjection.AnyGroundedRead([]).Should().BeFalse();
    }

    [Fact]
    public void A_call_that_failed_or_was_refused_grounds_nothing()
    {
        var observations = new[]
        {
            Observation(CoachToolNames.GetPracticeBalance, Scope(), outcome: CoachToolCallOutcome.Faulted),
            Observation(CoachToolNames.GetResourceCatalog, null, outcome: CoachToolCallOutcome.Refused)
        };

        CoachTurnEvidenceProjection.Project(observations, Today).Should().BeEmpty(
            "a read that threw or was refused produced no scope and supports no claim");
        CoachTurnEvidenceProjection.AnyGroundedRead(observations).Should().BeFalse();
    }

    [Fact]
    public void Each_population_the_turn_read_becomes_one_item_carrying_that_reads_own_counts()
    {
        var observations = new[]
        {
            Observation(CoachToolNames.GetPracticeBalance, Scope(
                definition: CoachScopeDefinition.PracticeWindowBalance,
                coverage: CoachScopeCoverage.WindowBounded,
                order: CoachScopeOrder.MinutesDescending,
                returned: 3,
                windowStart: Today.AddDays(-6),
                windowEnd: Today)),
            Observation(CoachToolNames.ListUserVocabularies, Scope(
                definition: CoachScopeDefinition.UndueVocabularySearch,
                coverage: CoachScopeCoverage.CompleteOwnedSet,
                order: CoachScopeOrder.MasteryDescending,
                returned: 10,
                matched: 14,
                withheld: 4,
                withheldReason: CoachScopeWithheldReason.DueReviewEmbargo))
        };

        var evidence = CoachTurnEvidenceProjection.Project(observations, Today);

        evidence.Should().HaveCount(2, "two populations were consulted");

        var balance = evidence.Single(e => e.Kind == CoachEvidenceKind.PracticeBalance);
        balance.WindowStartDate.Should().Be(Today.AddDays(-6));
        balance.WindowEndDate.Should().Be(Today);
        balance.Coverage.Should().Be(CoachEvidenceCoverage.WindowBounded);
        balance.Order.Should().Be(CoachEvidenceOrder.MinutesDescending);
        balance.DefinitionCode.Should().Be(CoachDefinitionCode.PracticeWindowBalance);
        balance.ReturnedCount.Should().Be(3);

        var vocabulary = evidence.Single(e => e.Kind == CoachEvidenceKind.VocabularyDue);
        vocabulary.MatchedCount.Should().Be(14);
        vocabulary.ReturnedCount.Should().Be(10);
        vocabulary.WithheldCount.Should().Be(4);
        vocabulary.WithheldReason.Should().Be(CoachWithheldReason.DueReviewEmbargo);
        vocabulary.AsOfUtc.Should().Be(Now, "whole-second, straight from the scope");

        vocabulary.Values.Should().SatisfyRespectively(
            v => v.Value.Should().Be(10),
            v => v.Value.Should().Be(14),
            v => v.Value.Should().Be(4));
    }

    [Fact]
    public void Reading_one_population_twice_shows_the_read_the_answer_rests_on()
    {
        var observations = new[]
        {
            Observation(CoachToolNames.ListUserVocabularies, Scope(
                definition: CoachScopeDefinition.UndueVocabularySearch, returned: 3, matched: 3)),
            Observation(CoachToolNames.ListUserVocabularies, Scope(
                definition: CoachScopeDefinition.UndueVocabularySearch, returned: 9, matched: 9), ordinal: 2)
        };

        var evidence = CoachTurnEvidenceProjection.Project(observations, Today);

        evidence.Should().ContainSingle("one population, one row on the panel");
        evidence[0].ReturnedCount.Should().Be(9, "the later read is the one the closing message rests on");
    }

    [Fact]
    public void Items_appear_in_the_order_the_populations_were_first_consulted()
    {
        var observations = new[]
        {
            Observation(CoachToolNames.GetResourceCatalog, Scope(
                definition: CoachScopeDefinition.OwnedResourceCatalog)),
            Observation(CoachToolNames.GetPracticeBalance, Scope(
                definition: CoachScopeDefinition.PracticeWindowBalance,
                windowStart: Today.AddDays(-6),
                windowEnd: Today), ordinal: 2),
            Observation(CoachToolNames.GetResourceCatalog, Scope(
                definition: CoachScopeDefinition.OwnedResourceCatalog, returned: 7), ordinal: 3)
        };

        var evidence = CoachTurnEvidenceProjection.Project(observations, Today);

        evidence.Select(e => e.Kind).Should().Equal(
            CoachEvidenceKind.ResourceCatalog,
            CoachEvidenceKind.PracticeBalance);
        evidence[0].ReturnedCount.Should().Be(7, "replaced in place, not appended");
    }

    [Fact]
    public void A_read_with_no_window_is_dated_to_the_day_it_was_made()
    {
        var evidence = CoachTurnEvidenceProjection.Project(
            [Observation(CoachToolNames.GetLearnerSettingsSummary, Scope(
                definition: CoachScopeDefinition.LearnerSettingsSnapshot))],
            Today);

        evidence.Should().ContainSingle();
        evidence[0].WindowStartDate.Should().Be(Today);
        evidence[0].WindowEndDate.Should().Be(Today, "the DTO contract is that evidence always states a range");
        evidence[0].AsOfUtc.Should().Be(Now, "the exact instant rides on AsOfUtc");
    }

    // =====================================================================
    // Grounding
    // =====================================================================

    [Fact]
    public void A_read_that_maps_to_no_evidence_bucket_still_counts_as_grounding()
    {
        // Skills have no CoachEvidenceKind member. A skills-only turn is genuinely grounded, and
        // rejecting it for a gap in a wire enum would refuse a truthful answer.
        var observations = new[]
        {
            Observation(CoachToolNames.GetSkillList, Scope(definition: CoachScopeDefinition.ActiveSkillList))
        };

        CoachTurnEvidenceProjection.AnyGroundedRead(observations).Should().BeTrue();
        CoachTurnEvidenceProjection.Project(observations, Today).Should().BeEmpty(
            "no honest bucket exists, and guessing one would mislabel the learner's own data");
    }

    // =====================================================================
    // Census: the definition map is total
    // =====================================================================

    [Fact]
    public void Every_definition_is_classified_and_every_bucket_has_words()
    {
        var classified = 0;
        var bucketed = 0;

        foreach (var definition in Enum.GetValues<CoachScopeDefinition>())
        {
            var kind = CoachTurnEvidenceProjection.ToEvidenceKind(definition);
            classified++;

            if (definition is CoachScopeDefinition.Unspecified
                or CoachScopeDefinition.ActiveSkillList
                or CoachScopeDefinition.ActiveSkillDetail)
            {
                kind.Should().BeNull("{0} has no honest evidence bucket", definition);
                continue;
            }

            kind.Should().NotBeNull("{0} names a population a learner can be shown", definition);
            CoachTurnEvidenceProjection.SummaryFor(definition).Should().NotBeNullOrWhiteSpace(
                "{0} must describe what was consulted", definition);
            bucketed++;
        }

        classified.Should().Be(14, "every definition must be classified; the sweep must see all of them");
        bucketed.Should().Be(11, "eleven definitions map to a learner-visible bucket");

        var labelled = 0;
        foreach (var kind in Enum.GetValues<CoachEvidenceKind>())
        {
            // Unrecognized is the client's tolerant-converter sentinel, appended for W3
            // localization. The server never emits it, and asking it for a heading is a bug rather
            // than a case to fall back from.
            if (kind == CoachEvidenceKind.Unrecognized)
            {
                var act = () => CoachTurnEvidenceProjection.LabelFor(kind);
                act.Should().Throw<ArgumentOutOfRangeException>(
                    "the server must never be asked to name a sentinel it cannot produce");
                continue;
            }

            CoachTurnEvidenceProjection.LabelFor(kind).Should().NotBeNullOrWhiteSpace("{0}", kind);
            labelled++;
        }

        labelled.Should().Be(5, "every evidence bucket needs a heading");
        Enum.GetValues<CoachEvidenceKind>().Should().HaveCount(
            6, "five buckets plus the appended client sentinel");
    }

    // =====================================================================
    // Embargo
    // =====================================================================

    [Fact]
    public void Nothing_a_scope_cannot_carry_can_enter_the_evidence_it_produces()
    {
        // The scope is structurally incapable of holding a term, so the projection cannot leak one.
        // Asserted on the serialized item rather than argued, because the claim is about the bytes
        // that leave the server.
        var observations = new[]
        {
            Observation(CoachToolNames.ListUserVocabularies, Scope(
                definition: CoachScopeDefinition.UndueVocabularySearch,
                returned: 10,
                matched: 14,
                withheld: 4,
                withheldReason: CoachScopeWithheldReason.DueReviewEmbargo))
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            CoachTurnEvidenceProjection.Project(observations, Today));

        json.Should().Contain("4", "the withheld count is the disclosure");
        foreach (var forbidden in new[] { "만기", "사과", "apple", "query", "transcript", "mnemonic" })
        {
            json.Should().NotContain(forbidden, "evidence is aggregate-only");
        }
    }

    [Fact]
    public void Every_projected_value_is_a_count_rather_than_a_measure_of_content()
    {
        var evidence = CoachTurnEvidenceProjection.Project(
            [Observation(CoachToolNames.ListUserVocabularies, Scope(
                definition: CoachScopeDefinition.UndueVocabularySearch,
                returned: 10, matched: 14, withheld: 4,
                withheldReason: CoachScopeWithheldReason.DueReviewEmbargo))],
            Today);

        evidence[0].Values.Should().NotBeEmpty(
            "the old projection attached no values at all, which is what made a fabricated card "
            + "indistinguishable from a real one");

        foreach (var value in evidence[0].Values)
        {
            value.Unit.Should().Be(CoachEvidenceUnit.Items);
            value.Value.Should().BeGreaterThanOrEqualTo(0);
            value.Label.Should().NotContain("\"", "a value label is fixed server copy, never learner text");
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static CoachToolCallObservation Observation(
        string toolName,
        CoachResultScope? scope,
        CoachToolCallOutcome outcome = CoachToolCallOutcome.Succeeded,
        int ordinal = 1) =>
        new(toolName, ordinal, outcome, null, CoachToolArgumentMask.None, ElapsedMs: 1, scope);

    private static CoachResultScope Scope(
        CoachScopeDefinition definition = CoachScopeDefinition.UndueVocabularySearch,
        CoachScopeCoverage coverage = CoachScopeCoverage.CompleteOwnedSet,
        CoachScopeOrder order = CoachScopeOrder.MasteryDescending,
        int returned = 1,
        int? matched = null,
        int withheld = 0,
        CoachScopeWithheldReason withheldReason = CoachScopeWithheldReason.None,
        DateOnly? windowStart = null,
        DateOnly? windowEnd = null) => new()
        {
            Coverage = coverage,
            Order = order,
            OrderHonored = true,
            Filters = CoachScopeFilters.OwnerScoped,
            AsOfUtc = Now,
            WindowStartDate = windowStart,
            WindowEndDate = windowEnd,
            ReturnedCount = returned,
            MatchedCount = matched,
            WithheldCount = withheld,
            WithheldReason = withheldReason,
            DefinitionCode = definition,
            MinimumEvidence = CoachScopeMinimumEvidence.None,
            TieBreak = CoachScopeTieBreak.None,
            ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
            ReferenceMode = CoachScopeReferenceMode.AsOfInstant
        };
}
