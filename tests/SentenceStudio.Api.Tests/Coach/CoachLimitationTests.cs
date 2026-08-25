using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.Limitations;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The two boundary answers, and the properties that make them honest rather than merely polite.
/// </summary>
/// <remarks>
/// <para>
/// S15 and S16 are the scenarios where a helpful coach does the most damage. S15 is a coach that
/// cheerfully deletes four hundred words because the learner asked nicely; S16 is a coach that
/// hands over today's answers because refusing felt unfriendly. Both are one accommodating
/// sentence away from happening, so the assertions below are about structure — what the shape
/// cannot contain and what it must — rather than about tone, which no test can hold.
/// </para>
/// <para>
/// The acceptance criteria these implement, verbatim from the plan: <b>AC-S15</b> no destructive
/// proposal is generated, consequences are stated, reversible alternatives are offered.
/// <b>AC-S16a</b> zero embargoed terms in the output. <b>AC-S16b</b> a hint-ladder alternative is
/// offered and no moral lecture appears. <b>AC-G2</b> side effects are disclosed before the
/// navigation.
/// </para>
/// </remarks>
public sealed class CoachLimitationTests
{
    private static readonly DateOnly ReviewDay = new(2026, 8, 21);

    private static readonly DateTime AsOf = new(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc);

    // ── S15: bulk vocabulary deletion ────────────────────────────────────────

    [Fact]
    public void S15_states_the_consequence_as_a_count()
    {
        var limitation = CoachLimitations.BulkVocabularyDeletion(
            412, AsOf, CoachEvidenceCoverage.CompleteOwnedSet);

        limitation.AffectedCount.Should().Be(
            412,
            "a learner weighing 'that would remove 412 words' is weighing a fact; a learner "
            + "weighing 'that would remove a lot' is weighing an adjective they can discount");

        limitation.Code.Should().Be(CoachLimitationCode.ExceedsSafeChangeScope);
        limitation.Coverage.Should().Be(
            CoachEvidenceCoverage.CompleteOwnedSet,
            "stated by the caller; the builder no longer assumes it");
    }

    [Fact]
    public void S15_offers_only_reversible_alternatives()
    {
        var limitation = CoachLimitations.BulkVocabularyDeletion(412, AsOf);

        limitation.Alternatives.Should().NotBeEmpty("AC-S15 requires alternatives, not just a refusal");

        limitation.Alternatives.Should().BeEquivalentTo(
            [
                CoachAlternativeCode.ExportBeforeRemoving,
                CoachAlternativeCode.RemoveOneListAtATime,
                CoachAlternativeCode.StartAFreshList
            ],
            options => options.WithStrictOrdering(),
            "each of these leaves a way back, and the export leads because it is the only one that "
            + "makes the learner's original request safe rather than smaller");
    }

    [Fact]
    public void S15_recommends_the_bounded_surface_and_still_names_the_account_one()
    {
        var limitation = CoachLimitations.BulkVocabularyDeletion(412, AsOf);

        limitation.Destination!.Route.Should().Be(
            CoachRouteName.Vocabulary,
            "the recommended screen is where the smallest reversible version of the request lives");

        limitation.FullScopeSurface.Should().BeNull(
            "Settings exports data and deletes coach history; it offers no account-level "
            + "start-clean, and naming a screen that cannot do the thing sends the learner "
            + "hunting for a control that does not exist");

        limitation.ExportSurface!.Route.Should().Be(
            CoachRouteName.Settings,
            "export is real, so the one screen S15 can honestly name is the export one");
    }

    /// <summary>AC-G2. Both surfaces disclose before the learner can act on either.</summary>
    [Fact]
    public void S15_discloses_the_side_effect_of_every_surface_it_names()
    {
        var limitation = CoachLimitations.BulkVocabularyDeletion(412, AsOf);

        limitation.Destination!.SideEffect.Should().Be(CoachRouteSideEffect.EditsLearnerData);
        limitation.ExportSurface!.SideEffect.Should().Be(
            CoachRouteSideEffect.EditsLearnerData,
            "Settings deletes coach conversation history, so its ceiling is not ChangesSettings");

        new[] { limitation.Destination, limitation.ExportSurface }.Should().OnlyContain(
            destination => destination!.SideEffect != CoachRouteSideEffect.Unknown,
            "an undisclosed consequence on a screen that can delete a learner's data is the exact "
            + "omission AC-G2 exists to prevent");
    }

    /// <summary>AC-S15. Nothing on this shape can express "here is the deletion, confirm it".</summary>
    [Fact]
    public void S15_generates_no_destructive_proposal()
    {
        var limitation = CoachLimitations.BulkVocabularyDeletion(412, AsOf);
        var json = JsonSerializer.Serialize(limitation, WireJson.Client);

        limitation.Alternatives.Should().NotContain(
            CoachAlternativeCode.Unknown,
            "an unnamed option beside a refusal is an invitation to act on something nobody can "
            + "describe");

        json.Should().NotContain("confirm", "a limitation is not a proposal and carries no confirmation");
        json.Should().NotContain("delete", "no member of this shape proposes a deletion");
    }

    [Fact]
    public void S15_normalizes_its_timestamp_to_whole_seconds()
    {
        var fractional = new DateTime(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc).AddTicks(4_821_593);

        var limitation = CoachLimitations.BulkVocabularyDeletion(412, fractional);

        limitation.AsOfUtc.Should().Be(
            AsOf,
            "truncated to the same whole second CoachResultScope uses, so a limitation and an "
            + "evidence item from one turn compare equal instead of differing in the seventh decimal");
        limitation.AsOfUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void S15_never_rounds_a_timestamp_forward()
    {
        var nearlyNextSecond = new DateTime(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc)
            .AddTicks(TimeSpan.TicksPerSecond - 1);

        CoachLimitations.BulkVocabularyDeletion(1, nearlyNextSecond).AsOfUtc.Should().Be(
            AsOf,
            "rounding up would place an 'as of' claim in the future, which is a claim about data "
            + "that did not exist when it was counted");
    }

    [Fact]
    public void S15_refuses_a_negative_count()
    {
        var act = () => CoachLimitations.BulkVocabularyDeletion(-1, AsOf);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── S16: review answer boundary ──────────────────────────────────────────

    [Fact]
    public void S16_refuses_disclosure_for_the_learning_reason()
    {
        var limitation = CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);

        limitation.Code.Should().Be(
            CoachLimitationCode.WouldRemoveLearningValue,
            "not NotBuilt and not RefusedByDesign: the answers exist and the refusal is about what "
            + "reading them would cost the learner, which is the only honest reason available");

        limitation.Coverage.Should().Be(
            CoachEvidenceCoverage.SingleDay,
            "the refusal speaks for today's review set, not for every word the learner owns");
    }

    /// <summary>AC-S16b. The ladder is offered, ascending, and complete.</summary>
    [Fact]
    public void S16_offers_a_three_rung_ladder_ascending_in_support()
    {
        var limitation = CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);

        limitation.HintLadder.Should().HaveCount(3);

        limitation.HintLadder.Select(rung => rung.Rung).Should().Equal(
            [1, 2, 3],
            "1-based and contiguous, so a client can render 'a bigger nudge' without inventing an "
            + "order the server did not state");

        limitation.HintLadder.Select(rung => rung.Kind).Should().Equal(
            [CoachHintKind.Category, CoachHintKind.Cloze, CoachHintKind.FormCue],
            "context before form. In Korean an initial character plus a length is very nearly the "
            + "answer for a two- or three-block target, so the form cue is the last rung, not the "
            + "middle one. Every rung still requires the learner to produce the form");

        // The property, not the sequence. Restating the order twice would pass with the order
        // reversed; this fails whenever a rung discloses less of the form than the one before it.
        limitation.HintLadder
            .Select(rung => CoachLimitations.FormDisclosureRank(rung.Kind))
            .Should().BeInAscendingOrder(
                "support may increase down the ladder, but never by revealing the form earlier");

        limitation.Alternatives.Should().Contain(
            CoachAlternativeCode.UseHintLadder,
            "AC-S16b requires the ladder to be offered as an alternative, not merely to exist");
    }

    /// <summary>
    /// AC-S16a. The strongest form of this guarantee: there is no field that could hold a term.
    /// </summary>
    /// <remarks>
    /// Asserting "the output contains no embargoed term" against sample data would only prove the
    /// sample was clean. Asserting that the shape has no string property at all proves that no
    /// input, no later change to hint generation, and no model output can put one there.
    /// </remarks>
    [Fact]
    public void S16_shape_cannot_carry_a_term()
    {
        var stringProperties = typeof(CoachHintRungDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        stringProperties.Should().BeEmpty(
            "a hint rung carries a rung number and a closed kind. A string here is where the term, "
            + "a gloss, or an example sentence would arrive. Offending: {0}",
            string.Join(", ", stringProperties));
    }

    [Fact]
    public void S16_output_contains_no_learner_content()
    {
        var limitation = CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);
        var json = JsonSerializer.Serialize(limitation, WireJson.Client);

        // Everything on the wire is a closed code, a count, or a timestamp. Nothing about the
        // learner's words survives serialization because nothing about them ever entered.
        json.Should().NotContain("term");
        json.Should().NotContain("translation");
        json.Should().NotContain("answer");
    }

    [Fact]
    public void S16_names_no_screen()
    {
        var limitation = CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);

        limitation.Destination.Should().BeNull(
            "naming a screen here would imply the answers are visible on one, which is false and "
            + "is the precise thing being refused");
        limitation.FullScopeSurface.Should().BeNull();
    }

    /// <summary>AC-S16b. The learner asked because the session is long. Answer that.</summary>
    [Fact]
    public void S16_offers_a_shorter_session_that_preserves_retrieval()
    {
        var limitation = CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);

        limitation.ShorterSession.Should().NotBeNull();
        limitation.ShorterSession!.SuggestedItemCount.Should().Be(6);
        limitation.ShorterSession.FullItemCount.Should().Be(18);

        limitation.ShorterSession.PreservesRetrieval.Should().BeTrue(
            "fewer items, not easier ones. Cutting difficulty would keep the count and remove the "
            + "learning, which is the trade this offer exists to refuse");

        limitation.Alternatives.Should().Contain(CoachAlternativeCode.TakeAShorterSession);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(4, 9)]
    [InlineData(4, 0)]
    public void S16_withholds_a_degenerate_shorter_session(int dueCount, int suggested)
    {
        var limitation = CoachLimitations.ReviewAnswerDisclosure(dueCount, suggested, AsOf, ReviewDay);

        limitation.ShorterSession.Should().BeNull(
            "an offer of the same length is not an offer, an offer of more is not shorter, and an "
            + "offer of nothing is the skip this scenario exists to give an alternative to");

        limitation.HintLadder.Should().HaveCount(
            3,
            "the ladder stands on its own when there is nothing shorter to offer");
    }

    [Fact]
    public void S16_ladder_is_shared_and_cannot_be_reordered_per_call()
    {
        var first = CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);
        var second = CoachLimitations.ReviewAnswerDisclosure(3, 1, AsOf.AddHours(2), ReviewDay);

        first.HintLadder.Should().BeSameAs(
            second.HintLadder,
            "one ladder for every S16 answer; a per-call ladder is a code path that can start at "
            + "the cloze, which is the rung nearest the answer");
    }

    // ── Copy carries no numbers ──────────────────────────────────────────────

    /// <summary>
    /// B11. Counts live in the DTO; the sentence lives in the copy; neither drifts into the other.
    /// </summary>
    /// <remarks>
    /// Reflection over the const values rather than a scan of the source text, because the source
    /// contains <c>\u2019</c> escapes and a naive digit scan would flag the apostrophes rather than
    /// the counts.
    /// </remarks>
    [Fact]
    public void Deterministic_copy_constants_contain_no_digits_or_dates()
    {
        var constants = typeof(CoachDeterministicCopy)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false })
            .Where(field => field.FieldType == typeof(string))
            .ToArray();

        constants.Should().NotBeEmpty("a scan over no constants proves nothing");

        foreach (var constant in constants)
        {
            var value = (string)constant.GetRawConstantValue()!;

            value.Should().NotMatchRegex(
                @"\d",
                "{0} contains a digit. A count inside a sentence is a count no test can check and "
                + "no translator can keep true — and a fluent sentence with a wrong number in it is "
                + "this coach's documented failure mode",
                constant.Name);

            Regex.IsMatch(value, @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\b")
                .Should().BeFalse("{0} names a month; dates belong on the DTO", constant.Name);
        }
    }

    /// <summary>The four limitation strings exist, are non-empty, and say nothing numeric.</summary>
    [Theory]
    [InlineData(nameof(CoachDeterministicCopy.BulkVocabularyDeletionRefusal))]
    [InlineData(nameof(CoachDeterministicCopy.BulkVocabularyDeletionRedirect))]
    [InlineData(nameof(CoachDeterministicCopy.BulkVocabularyDeletionExportSurface))]
    [InlineData(nameof(CoachDeterministicCopy.ReviewAnswerRefusal))]
    [InlineData(nameof(CoachDeterministicCopy.ReviewAnswerHintLadderOffer))]
    [InlineData(nameof(CoachDeterministicCopy.ReviewAnswerShorterSessionOffer))]
    public void Limitation_copy_exists_and_is_count_free(string name)
    {
        var field = typeof(CoachDeterministicCopy).GetField(name, BindingFlags.Public | BindingFlags.Static);

        field.Should().NotBeNull();

        var value = (string)field!.GetRawConstantValue()!;

        value.Should().NotBeNullOrWhiteSpace();
        value.Should().NotMatchRegex(@"\d");
        value.Should().NotContain("{", "an interpolation placeholder is a count in disguise");
    }

    /// <summary>
    /// AC-S16b. No moral lecture. Checked against the words a lecture is actually built from.
    /// </summary>
    [Fact]
    public void S16_copy_does_not_lecture()
    {
        string[] copy =
        [
            CoachDeterministicCopy.ReviewAnswerRefusal,
            CoachDeterministicCopy.ReviewAnswerHintLadderOffer,
            CoachDeterministicCopy.ReviewAnswerShorterSessionOffer
        ];

        // A lecture is not a tone, it is a vocabulary: it tells the learner what they should want,
        // implies they are cheating themselves, or grades the request.
        string[] lectureWords =
        [
            "should", "shouldn't", "cheat", "cheating", "lazy", "discipline", "honest",
            "really want", "won't help you", "defeats", "pointless", "waste"
        ];

        foreach (var sentence in copy)
        {
            foreach (var word in lectureWords)
            {
                sentence.Should().NotContainEquivalentOf(
                    word,
                    "AC-S16b forbids a moral lecture, and the learner asking for answers is telling "
                    + "you the session is too long — not confessing to something");
            }
        }
    }

    // ── Architecture: the limitation layer reaches no database ───────────────

    /// <summary>
    /// W7 takes its counts as parameters and owns no data access.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is "application services only, no DbContext under Coach". Asserted structurally
    /// rather than trusted, because the shortest path to a truthful count is always to query for it
    /// right here — and a limitation builder that owned a DbContext would be untestable without a
    /// database, unrenderable in a synthetic acceptance run, and one refactor away from doing a
    /// query inside a refusal.
    /// </para>
    /// <para>
    /// Every count therefore arrives from a caller that already holds the service which produced it,
    /// and this type stays a pure function of its arguments.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_limitation_builder_takes_no_dependencies()
    {
        var type = typeof(CoachLimitations);

        type.IsAbstract.Should().BeTrue();
        type.IsSealed.Should().BeTrue("a static class cannot be constructed with a DbContext");

        var parameterTypes = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Distinct()
            .ToArray();

        parameterTypes.Should().NotBeEmpty("a builder with no parameters cannot state a truthful count");

        parameterTypes.Should().OnlyContain(
            parameterType => parameterType.IsPrimitive
                             || parameterType.IsEnum
                             || parameterType == typeof(DateTime)
                             || parameterType == typeof(DateOnly),
            "the builder accepts counts, codes, an instant and a calendar day. A service, a context "
            + "or a repository in this list would mean the refusal path reaches storage");
    }

    /// <summary>
    /// No source file under the limitation layer names a DbContext or a DbSet.
    /// </summary>
    [Fact]
    public void The_limitation_layer_references_no_database_type()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Api")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();

        var limitationLayer = Path.Combine(
            directory!.FullName, "src", "SentenceStudio.Api", "Coach", "Application", "Limitations");

        var sources = Directory.GetFiles(limitationLayer, "*.cs", SearchOption.AllDirectories);

        sources.Should().NotBeEmpty("a scan over no files proves nothing");

        foreach (var source in sources)
        {
            // Comments are stripped first. The rule is about what the code reaches, and the file's
            // own remarks explain why it reaches nothing — a scan that flagged the explanation
            // would push the next author to delete the reasoning rather than keep the boundary.
            var code = StripComments(File.ReadAllText(source));

            code.Should().NotContain("DbContext", "{0} must not reach storage", Path.GetFileName(source));
            code.Should().NotContain("DbSet", "{0} must not reach storage", Path.GetFileName(source));
            code.Should().NotContain(
                "Repository",
                "{0} takes its counts as parameters from a caller that already has them",
                Path.GetFileName(source));
        }
    }

    /// <summary>Removes line and block comments so a source scan reads code, not prose.</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"^[ \t]*///?.*$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>
    /// W7 supplies metadata. It does not navigate, and it cannot express an execution.
    /// </summary>
    [Fact]
    public void A_limitation_cannot_express_a_command()
    {
        var members = typeof(CoachLimitationDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        members.Should().NotBeEmpty();

        foreach (var forbidden in new[] { "Execute", "Navigate", "Command", "Confirm", "Apply" })
        {
            members.Should().NotContain(
                member => member.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "a limitation describes what will not happen and where the learner can act. A "
                + "member named for {0} would make it an instruction, and under AC-G1 an "
                + "ungestured navigation is an unauthorized one",
                forbidden);
        }
    }
}
