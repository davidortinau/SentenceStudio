using SentenceStudio.Api.Coach.Evidence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Evidence;

/// <summary>
/// The mirror between the server's scope vocabulary and the wire's, checked rather than reviewed.
/// </summary>
/// <remarks>
/// <para>
/// Mirroring the scope enums into Contracts was a deliberate trade: moving them would have touched
/// every coach tool and every scope test at once, in files three workstreams are inside. The price
/// is that two lists now have to agree, and lists that have to agree drift. This file is the thing
/// that stops them.
/// </para>
/// <para>
/// <b>Every sweep states its census.</b> A guard that walks zero members passes, and a mapper
/// audited by a vacuous guard is a mapper nobody audited. So each sweep asserts the number of
/// members it examined, and that number is written out rather than derived from the same
/// <c>GetValues</c> call being tested.
/// </para>
/// <para>
/// <b>Every rule has a failing fixture.</b> The census helpers are run against deliberately broken
/// mappings at the bottom of this file, because a completeness check that has never been seen to
/// fail is a completeness check nobody has tested.
/// </para>
/// </remarks>
public class CoachEvidenceScopeProjectionTests
{
    // =====================================================================
    // Completeness: no server member maps to Unknown
    // =====================================================================

    [Fact]
    public void Every_coverage_the_server_can_state_reaches_a_named_wire_value()
    {
        var swept = Sweep<CoachScopeCoverage, CoachEvidenceCoverage>(
            CoachEvidenceScopeProjection.ToWire,
            CoachScopeCoverage.Unspecified,
            CoachEvidenceCoverage.Unknown);

        swept.Should().Be(9, "CoachScopeCoverage has nine members and every one must be swept");
    }

    [Fact]
    public void Every_order_the_server_can_state_reaches_a_named_wire_value()
    {
        var swept = Sweep<CoachScopeOrder, CoachEvidenceOrder>(
            CoachEvidenceScopeProjection.ToWire,
            CoachScopeOrder.Unspecified,
            CoachEvidenceOrder.Unknown);

        swept.Should().Be(10, "CoachScopeOrder has ten members and every one must be swept");
    }

    [Fact]
    public void Every_definition_the_server_can_state_reaches_a_named_wire_value()
    {
        var swept = Sweep<CoachScopeDefinition, CoachDefinitionCode>(
            CoachEvidenceScopeProjection.ToWire,
            CoachScopeDefinition.Unspecified,
            CoachDefinitionCode.Unknown);

        swept.Should().Be(15, "CoachScopeDefinition has fifteen members and every one must be swept");
    }

    [Fact]
    public void Every_withheld_reason_the_server_can_state_reaches_a_named_wire_value()
    {
        // No Unspecified member here: the server's zero is None, which is a real claim and maps to
        // the wire's own None. Nothing at all may land on Unknown.
        var members = Enum.GetValues<CoachScopeWithheldReason>();
        members.Should().HaveCount(5);

        foreach (var member in members)
        {
            CoachEvidenceScopeProjection.ToWire(member).Should().NotBe(
                CoachWithheldReason.Unknown,
                "{0} is a reason the server states; mapping it to Unknown would drop the explanation "
                + "for a disclosure the learner is looking at", member);
        }
    }

    // =====================================================================
    // Fidelity: the mirror is a mirror, not a rearrangement
    // =====================================================================

    [Fact]
    public void Each_server_member_maps_to_the_wire_member_of_the_same_name()
    {
        var checkedPairs = 0;

        checkedPairs += NameParity<CoachScopeCoverage, CoachEvidenceCoverage>(
            CoachEvidenceScopeProjection.ToWire, CoachScopeCoverage.Unspecified);
        checkedPairs += NameParity<CoachScopeOrder, CoachEvidenceOrder>(
            CoachEvidenceScopeProjection.ToWire, CoachScopeOrder.Unspecified);
        checkedPairs += NameParity<CoachScopeDefinition, CoachDefinitionCode>(
            CoachEvidenceScopeProjection.ToWire, CoachScopeDefinition.Unspecified);
        checkedPairs += NameParity<CoachScopeWithheldReason, CoachWithheldReason>(
            CoachEvidenceScopeProjection.ToWire, skip: null);

        checkedPairs.Should().Be(
            8 + 9 + 14 + 5,
            "the mirror is checked name by name; a mis-wired arm such as SingleItem => SingleDay "
            + "passes every completeness check and is only visible here");
    }

    [Fact]
    public void The_three_ordinal_aligned_enums_keep_their_numbering()
    {
        // Held equal on purpose, so a reviewer can check the mirror by reading two lists side by
        // side. Only WithheldReason departs from this, and it says why in place.
        AssertOrdinalMirror<CoachScopeCoverage, CoachEvidenceCoverage>(9);
        AssertOrdinalMirror<CoachScopeOrder, CoachEvidenceOrder>(10);
        AssertOrdinalMirror<CoachScopeDefinition, CoachDefinitionCode>(15);
    }

    [Fact]
    public void The_withheld_reason_numbering_is_deliberately_shifted_by_one()
    {
        // Asserted rather than assumed, so nobody "fixes" the inconsistency later. The server's
        // zero is None — nothing was withheld — and the wire's zero has to mean "no claim
        // readable". Collapsing an unreadable reason onto None would tell the learner nothing was
        // held back at the exact moment the client had lost track of whether anything was.
        var pairs = 0;

        foreach (var member in Enum.GetValues<CoachScopeWithheldReason>())
        {
            var wire = CoachEvidenceScopeProjection.ToWire(member);
            ((int)wire).Should().Be((int)member + 1, "{0}", member);
            pairs++;
        }

        pairs.Should().Be(5);
        CoachEvidenceScopeProjection.ToWire(CoachScopeWithheldReason.None)
            .Should().Be(CoachWithheldReason.None);
    }

    [Fact]
    public void An_unspecified_scope_value_becomes_an_unknown_wire_value()
    {
        CoachEvidenceScopeProjection.ToWire(CoachScopeCoverage.Unspecified)
            .Should().Be(CoachEvidenceCoverage.Unknown);
        CoachEvidenceScopeProjection.ToWire(CoachScopeOrder.Unspecified)
            .Should().Be(CoachEvidenceOrder.Unknown);
        CoachEvidenceScopeProjection.ToWire(CoachScopeDefinition.Unspecified)
            .Should().Be(CoachDefinitionCode.Unknown);
    }

    [Fact]
    public void A_value_that_is_not_a_member_of_its_own_enum_is_refused_rather_than_mapped()
    {
        // Cast in from an integer. There is no path in the server that produces one, which is
        // exactly why it must throw: a scope carrying a value outside its own vocabulary is a
        // defect upstream, and reporting it as Unknown would launder it into "no claim".
        var rogue = () => CoachEvidenceScopeProjection.ToWire((CoachScopeCoverage)99);

        rogue.Should().Throw<Exception>();
    }

    // =====================================================================
    // WithScope
    // =====================================================================

    [Fact]
    public void WithScope_attaches_the_terms_without_touching_the_answer()
    {
        var evidence = SampleEvidence();
        var scope = SampleScope();

        var projected = CoachEvidenceScopeProjection.WithScope(evidence, scope);

        projected.Kind.Should().Be(evidence.Kind);
        projected.Label.Should().Be(evidence.Label);
        projected.Summary.Should().Be(evidence.Summary);
        projected.WindowStartDate.Should().Be(evidence.WindowStartDate);
        projected.WindowEndDate.Should().Be(evidence.WindowEndDate);
        projected.Values.Should().BeSameAs(evidence.Values);

        projected.Coverage.Should().Be(CoachEvidenceCoverage.PageOfOwnedSet);
        projected.Order.Should().Be(CoachEvidenceOrder.MasteryDescending);
        projected.DefinitionCode.Should().Be(CoachDefinitionCode.UndueVocabularySearch);
        projected.WithheldReason.Should().Be(CoachWithheldReason.DueReviewEmbargo);
        projected.MatchedCount.Should().Be(14);
        projected.ReturnedCount.Should().Be(10);
        projected.WithheldCount.Should().Be(4);

        evidence.Coverage.Should().BeNull("the source item is left alone");
    }

    [Fact]
    public void WithScope_carries_the_fourteen_ten_four_disclosure_without_a_single_word()
    {
        var projected = CoachEvidenceScopeProjection.WithScope(SampleEvidence(), SampleScope());

        // The whole point of the withheld count: the learner is told four of their own words are
        // being held back, and the four words stay on the server.
        projected.MatchedCount.Should().Be(14);
        projected.ReturnedCount.Should().Be(10);
        projected.WithheldCount.Should().Be(4);
        projected.WithheldReason.Should().Be(CoachWithheldReason.DueReviewEmbargo);

        var json = System.Text.Json.JsonSerializer.Serialize(projected);
        json.Should().NotContain("만기", "a withheld term must never ride out with its own count");
        json.Should().NotContain("사과");
    }

    [Fact]
    public void WithScope_normalizes_the_instant_the_same_way_the_scope_does()
    {
        var noisy = new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc).AddTicks(4_821_593);
        var scope = SampleScope(asOfUtc: noisy);

        var projected = CoachEvidenceScopeProjection.WithScope(SampleEvidence(), scope);

        projected.AsOfUtc.Should().Be(new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc));
        projected.AsOfUtc.Should().Be(scope.AsOfUtc, "the scope already truncated; the DTO agrees");
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(9_999_999L)]
    [InlineData(4_821_593L)]
    [InlineData(0L)]
    public void The_wire_normalizer_and_the_scope_normalizer_agree(long extraTicks)
    {
        // Contracts cannot reference Api, so the whole-second rule exists twice. This is the only
        // place both copies are visible at once, which makes it the only place they can be held
        // equal. A divergence would show as two different timestamps for one fact.
        var instant = new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc).AddTicks(extraTicks);

        CoachEvidenceInstant.Normalize(instant)
            .Should().Be(CoachResultScope.NormalizeAsOf(instant));

        var local = instant.ToLocalTime();
        CoachEvidenceInstant.Normalize(local).Should().Be(CoachResultScope.NormalizeAsOf(local));

        var unspecified = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified);
        CoachEvidenceInstant.Normalize(unspecified)
            .Should().Be(CoachResultScope.NormalizeAsOf(unspecified));
    }

    // =====================================================================
    // The guards, proven to fail
    // =====================================================================

    [Fact]
    public void The_completeness_sweep_catches_a_member_that_falls_through_to_unknown()
    {
        var broken = (CoachScopeCoverage value) => value switch
        {
            CoachScopeCoverage.CompleteOwnedSet => CoachEvidenceCoverage.CompleteOwnedSet,
            _ => CoachEvidenceCoverage.Unknown
        };

        var sweep = () => Sweep<CoachScopeCoverage, CoachEvidenceCoverage>(
            broken, CoachScopeCoverage.Unspecified, CoachEvidenceCoverage.Unknown);

        sweep.Should().Throw<Exception>(
            "a mapper that answers Unknown for a coverage the server states would render as no "
            + "coverage claim at all — the exact silence the field exists to remove");
    }

    [Fact]
    public void The_name_parity_sweep_catches_an_arm_wired_to_the_wrong_member()
    {
        var swapped = (CoachScopeCoverage value) => value switch
        {
            CoachScopeCoverage.SingleItem => CoachEvidenceCoverage.SingleDay,
            CoachScopeCoverage.Unspecified => CoachEvidenceCoverage.Unknown,
            _ => CoachEvidenceCoverage.CompleteOwnedSet
        };

        var sweep = () => NameParity<CoachScopeCoverage, CoachEvidenceCoverage>(
            swapped, CoachScopeCoverage.Unspecified);

        sweep.Should().Throw<Exception>(
            "one item described as one day is a wrong claim that every completeness check accepts");
    }

    [Fact]
    public void The_census_catches_a_sweep_that_examined_nothing()
    {
        // The failure mode this whole file is written against: a guard that walks an empty
        // population and reports success.
        var empty = Array.Empty<CoachScopeCoverage>().Length;

        var assertion = () => empty.Should().Be(9);

        assertion.Should().Throw<Exception>();
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// Maps every member and refuses any that lands on the wire's unknown value, apart from the
    /// server's own unspecified member. Returns how many were examined.
    /// </summary>
    private static int Sweep<TSource, TWire>(
        Func<TSource, TWire> map,
        TSource unspecified,
        TWire unknown)
        where TSource : struct, Enum
        where TWire : struct, Enum
    {
        var swept = 0;

        foreach (var member in Enum.GetValues<TSource>())
        {
            var wire = map(member);

            if (member.Equals(unspecified))
            {
                wire.Should().Be(unknown, "{0} is the server's own unset value", member);
            }
            else
            {
                wire.Should().NotBe(
                    unknown,
                    "{0} is a value the server states, so the client must be able to name it",
                    member);
            }

            swept++;
        }

        return swept;
    }

    /// <summary>
    /// Asserts each member maps to the wire member with the identical name. Returns the count of
    /// pairs checked, excluding <paramref name="skip"/>.
    /// </summary>
    private static int NameParity<TSource, TWire>(Func<TSource, TWire> map, TSource? skip)
        where TSource : struct, Enum
        where TWire : struct, Enum
    {
        var pairs = 0;

        foreach (var member in Enum.GetValues<TSource>())
        {
            if (skip is { } excluded && member.Equals(excluded))
            {
                continue;
            }

            map(member).ToString().Should().Be(
                member.ToString(),
                "the wire mirrors the server's vocabulary name for name");

            pairs++;
        }

        return pairs;
    }

    private static void AssertOrdinalMirror<TSource, TWire>(int expectedCount)
        where TSource : struct, Enum
        where TWire : struct, Enum
    {
        var source = Enum.GetValues<TSource>();
        var wire = Enum.GetValues<TWire>();

        source.Should().HaveCount(expectedCount);
        wire.Should().HaveCount(expectedCount, "the mirror must not gain or lose a member");

        for (var i = 0; i < source.Length; i++)
        {
            Convert.ToInt32(wire[i]).Should().Be(
                Convert.ToInt32(source[i]),
                "{0} and {1} are held ordinal-aligned so the mirror can be checked by reading",
                typeof(TSource).Name,
                typeof(TWire).Name);
        }
    }

    private static CoachEvidenceDto SampleEvidence() => new()
    {
        Kind = CoachEvidenceKind.VocabularyDue,
        Label = "Vocabulary",
        Summary = "Ten words are ready to practise.",
        WindowStartDate = new DateOnly(2026, 8, 1),
        WindowEndDate = new DateOnly(2026, 8, 14),
        Values = [new CoachEvidenceValueDto { Label = "Ready", Value = 10, Unit = CoachEvidenceUnit.Count }]
    };

    private static CoachResultScope SampleScope(DateTime? asOfUtc = null) => new()
    {
        Coverage = CoachScopeCoverage.PageOfOwnedSet,
        Order = CoachScopeOrder.MasteryDescending,
        OrderHonored = true,
        Filters = CoachScopeFilters.OwnerScoped
            | CoachScopeFilters.ProgressRowExists
            | CoachScopeFilters.ExcludeDue,
        AsOfUtc = asOfUtc ?? new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc),
        RequestedCount = 10,
        ReturnedCount = 10,
        MatchedCount = 14,
        WithheldCount = 4,
        WithheldReason = CoachScopeWithheldReason.DueReviewEmbargo,
        Truncated = false,
        DefinitionCode = CoachScopeDefinition.UndueVocabularySearch,
        EligiblePopulationCount = 10,
        MinimumEvidence = CoachScopeMinimumEvidence.ProgressRowRequired,
        TieBreak = CoachScopeTieBreak.None,
        ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
        ReferenceMode = CoachScopeReferenceMode.AsOfInstant
    };
}
