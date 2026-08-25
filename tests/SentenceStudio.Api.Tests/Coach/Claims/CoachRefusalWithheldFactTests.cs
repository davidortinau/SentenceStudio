using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The withheld pair a refusal carries, and the cases where it must carry nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the limitation repeats a fact the evidence already has.</b> The evidence rows are the
/// richer answer and a client should prefer them — but they are not reconstructed on every path. A
/// resumed session restores the stored limitation from the protected outcome without rebuilding the
/// evidence list, and a refusal that says "I held some back" with no number is the vaguer half of
/// what the server already knew.
/// </para>
/// <para>
/// <b>Why two reads produce nothing rather than a sum.</b> A vocabulary search that held back four
/// due terms and a due summary that held back two are not six of anything: no read computed a union,
/// the two sets may overlap, and "6 held back" would be a number the server invented. That is the
/// fluent arithmetic the whole grounding layer exists to prevent.
/// </para>
/// </remarks>
public sealed class CoachRefusalWithheldFactTests
{
    private static readonly DateTime AsOf = new(2026, 8, 22, 7, 30, 0, DateTimeKind.Utc);

    // ────────────────────────────────────────────────── the coherent pair

    [Theory]
    [InlineData(CoachWithheldReason.DueReviewEmbargo, 4)]
    [InlineData(CoachWithheldReason.BelowMinimumEvidence, 2)]
    [InlineData(CoachWithheldReason.ResultLimit, 11)]
    [InlineData(CoachWithheldReason.ArchivedExcluded, 1)]
    public void One_read_that_held_rows_back_states_the_exact_count_and_reason(
        CoachWithheldReason reason,
        int withheld)
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [Evidence(withheld: withheld, reason: reason)], AsOf);

        limitation.WithheldCount.Should().Be(withheld);
        limitation.WithheldReason.Should().Be(reason);
    }

    [Fact]
    public void Every_real_reason_is_representable()
    {
        // Non-vacuity for the theory above: a sixth reason added later without a case here fails.
        Enum.GetValues<CoachWithheldReason>()
            .Where(reason => reason is not (CoachWithheldReason.Unknown or CoachWithheldReason.None))
            .Should().HaveCount(4);
    }

    [Fact]
    public void A_single_coherent_pair_among_several_reads_is_unambiguous()
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [
                Evidence(withheld: null, reason: null),
                Evidence(withheld: 0, reason: CoachWithheldReason.None),
                Evidence(withheld: 3, reason: CoachWithheldReason.DueReviewEmbargo)
            ],
            AsOf);

        limitation.WithheldCount.Should().Be(
            3, "only one read held anything back, so there is one population and one number");
        limitation.WithheldReason.Should().Be(CoachWithheldReason.DueReviewEmbargo);
    }

    // ──────────────────────────────────────────────────── fail closed

    [Fact]
    public void A_turn_that_read_nothing_states_no_withheld_fact()
    {
        var limitation = CoachRefusalLimitationProjection.Project([], AsOf);

        limitation.WithheldCount.Should().BeNull();
        limitation.WithheldReason.Should().BeNull();
        limitation.Code.Should().Be(
            CoachLimitationCode.UnverifiedClaimWithheld, "the refusal itself is unchanged");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void A_read_that_held_nothing_back_states_nothing(int? withheld)
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [Evidence(withheld: withheld, reason: CoachWithheldReason.DueReviewEmbargo)], AsOf);

        limitation.WithheldCount.Should().BeNull("zero is not a withholding");
        limitation.WithheldReason.Should().BeNull("and a reason with no count states no scale");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CoachWithheldReason.None)]
    [InlineData(CoachWithheldReason.Unknown)]
    [InlineData((CoachWithheldReason)99)]
    public void A_count_with_no_usable_reason_states_nothing(CoachWithheldReason? reason)
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [Evidence(withheld: 4, reason: reason)], AsOf);

        limitation.WithheldCount.Should().BeNull(
            "'4 held back' with no because cannot be rendered as a sentence");
        limitation.WithheldReason.Should().BeNull();
    }

    [Fact]
    public void Two_reads_with_different_reasons_state_nothing()
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [
                Evidence(withheld: 4, reason: CoachWithheldReason.DueReviewEmbargo),
                Evidence(withheld: 2, reason: CoachWithheldReason.BelowMinimumEvidence)
            ],
            AsOf);

        limitation.WithheldCount.Should().BeNull();
        limitation.WithheldReason.Should().BeNull("two reasons cannot collapse into one");
    }

    [Fact]
    public void Two_reads_with_the_same_reason_still_state_nothing()
    {
        // The case that looks summable and is not. Two reads over two populations that may overlap;
        // no read computed a union, so six is a number the server would be inventing.
        var limitation = CoachRefusalLimitationProjection.Project(
            [
                Evidence(withheld: 4, reason: CoachWithheldReason.DueReviewEmbargo),
                Evidence(withheld: 2, reason: CoachWithheldReason.DueReviewEmbargo)
            ],
            AsOf);

        limitation.WithheldCount.Should().BeNull();
        limitation.WithheldReason.Should().BeNull();
    }

    [Fact]
    public void One_incoherent_read_poisons_the_whole_turns_withheld_picture()
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [
                Evidence(withheld: 4, reason: CoachWithheldReason.DueReviewEmbargo),
                Evidence(withheld: 2, reason: null)
            ],
            AsOf);

        limitation.WithheldCount.Should().BeNull(
            "the server cannot say how much was held back overall when one holding has no "
            + "explanation");
    }

    // ───────────────────────────────────────────── shape and tolerance

    [Fact]
    public void The_pair_carries_no_text_of_any_kind()
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [Evidence(withheld: 4, reason: CoachWithheldReason.DueReviewEmbargo)], AsOf);

        var json = JsonSerializer.Serialize(limitation, WireJson.Client);

        foreach (var forbidden in new[] { "gloss", "lemma", "example", "term", "transcript" })
        {
            json.Should().NotContain(forbidden);
        }

        typeof(CoachLimitationDto).GetProperty(nameof(CoachLimitationDto.WithheldCount))!
            .PropertyType.Should().Be(typeof(int?));
        typeof(CoachLimitationDto).GetProperty(nameof(CoachLimitationDto.WithheldReason))!
            .PropertyType.Should().Be(typeof(CoachWithheldReason?), "the closed evidence vocabulary, reused");
    }

    [Fact]
    public void An_old_client_ignores_the_new_members()
    {
        var json = JsonSerializer.Serialize(
            CoachRefusalLimitationProjection.Project(
                [Evidence(withheld: 4, reason: CoachWithheldReason.DueReviewEmbargo)], AsOf),
            WireJson.Client);

        json.Should().Contain("withheldCount");
        json.Should().Contain(nameof(CoachWithheldReason.DueReviewEmbargo));

        // Additive: a payload without the members still deserializes, with both null.
        var older = JsonSerializer.Deserialize<CoachLimitationDto>(
            """{"code":"UnverifiedClaimWithheld"}""", WireJson.Client);

        older!.WithheldCount.Should().BeNull();
        older.WithheldReason.Should().BeNull();
    }

    [Fact]
    public void An_unrecognised_reason_decodes_through_the_shared_fallback()
    {
        var reread = JsonSerializer.Deserialize<CoachLimitationDto>(
            """{"code":"UnverifiedClaimWithheld","withheldCount":4,"withheldReason":"SomethingNewer"}""",
            WireJson.Client);

        reread!.WithheldReason.Should().Be(
            CoachWithheldReason.Unknown,
            "the same tolerance the evidence rows already get, because it is the same enum");
        reread.WithheldCount.Should().Be(4);
    }

    [Fact]
    public void The_pair_survives_a_protected_outcome_round_trip()
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [Evidence(withheld: 4, reason: CoachWithheldReason.DueReviewEmbargo)], AsOf);

        // The same serializer the stored outcome uses, so this is the resume path's fidelity.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var reread = JsonSerializer.Deserialize<CoachLimitationDto>(
            JsonSerializer.Serialize(limitation, options), options);

        reread!.WithheldCount.Should().Be(4);
        reread.WithheldReason.Should().Be(CoachWithheldReason.DueReviewEmbargo);
        reread.Code.Should().Be(CoachLimitationCode.UnverifiedClaimWithheld);
    }

    [Fact]
    public void The_destination_and_the_rest_of_the_limitation_are_unchanged()
    {
        var withPair = CoachRefusalLimitationProjection.Project(
            [Evidence(withheld: 4, reason: CoachWithheldReason.DueReviewEmbargo)], AsOf);

        var withoutPair = CoachRefusalLimitationProjection.Project(
            [Evidence(withheld: null, reason: null)], AsOf);

        foreach (var limitation in new[] { withPair, withoutPair })
        {
            limitation.Code.Should().Be(CoachLimitationCode.UnverifiedClaimWithheld);
            limitation.Destination!.Route.Should().Be(CoachRouteName.Vocabulary);
            limitation.HintLadder.Should().BeEmpty();
            limitation.Alternatives.Should().BeEmpty();
            limitation.FullScopeSurface.Should().BeNull();
        }
    }

    // ─────────────────────────────────────────────────────── helpers

    private static CoachEvidenceDto Evidence(int? withheld, CoachWithheldReason? reason) => new()
    {
        Kind = CoachEvidenceKind.VocabularyDue,
        Label = "Vocabulary",
        Summary = "Words you are tracking.",
        WindowStartDate = new DateOnly(2026, 8, 1),
        WindowEndDate = new DateOnly(2026, 8, 22),
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        Order = CoachEvidenceOrder.MasteryDescending,
        DefinitionCode = CoachDefinitionCode.UndueVocabularySearch,
        MatchedCount = 14,
        ReturnedCount = 10,
        WithheldCount = withheld,
        WithheldReason = reason,
        AsOfUtc = AsOf
    };
}
