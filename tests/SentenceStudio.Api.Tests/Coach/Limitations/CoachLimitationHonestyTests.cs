using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.Limitations;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Limitations;

/// <summary>
/// The W7 revision: a limitation may only name a screen that exists and only offer a thing the app
/// can do.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> The first draft told a learner that a start-clean "lives in your
/// settings" — it does not; Settings exports data and deletes coach history, and no screen in this
/// build deletes an account. It offered to archive vocabulary, which only skill profiles can do,
/// and to pause reviews, which nothing does. It defaulted a partial count to
/// <c>CompleteOwnedSet</c>. It claimed <c>SingleDay</c> coverage without naming a day. And it
/// accepted any string as a route parameter, so <c>사과?answer=apple</c> was a legal vocabulary id.
/// </para>
/// <para>
/// Each of those is a confident, checkable falsehood delivered through the shape whose entire
/// purpose is to be the honest one. These tests are the checks that were missing.
/// </para>
/// </remarks>
public sealed class CoachLimitationHonestyTests
{
    private static readonly DateTime AsOf = new(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc);
    private static readonly DateOnly ReviewDay = new(2026, 8, 21);

    // ------------------------------------------- B1: every named route is a real page

    /// <summary>Every catalogue route resolves to a shipped <c>@page</c> directive.</summary>
    [Fact]
    public void Every_catalogue_route_is_a_page_the_app_actually_serves()
    {
        var pages = ShippedPageRoutes();
        pages.Should().NotBeEmpty("the scan must be reading real Razor pages");

        var expected = new Dictionary<CoachRouteName, string>
        {
            [CoachRouteName.ActivityLog] = "/activity-log",
            [CoachRouteName.Vocabulary] = "/vocabulary",
            [CoachRouteName.Settings] = "/settings",
            [CoachRouteName.Skills] = "/skills",
            [CoachRouteName.Writing] = "/writing",
            [CoachRouteName.Feedback] = "/feedback"
        };

        // The six the plan binds, and no seventh. A route added to the catalogue without a page
        // fails the mapping below; a route added without an entry here fails this count.
        CoachRouteCatalog.All.Should().HaveCount(6);
        expected.Should().HaveCount(6);

        var checkedRoutes = 0;
        foreach (var (route, path) in expected)
        {
            CoachRouteCatalog.All.Should().ContainKey(route);
            pages.Should().Contain(path,
                $"{route} is offered to learners as a destination, so the page has to exist");
            checkedRoutes++;
        }

        checkedRoutes.Should().Be(6);
    }

    [Fact]
    public void Settings_does_not_offer_a_start_clean_so_S15_names_no_whole_data_screen()
    {
        var settings = SettingsPageSource();

        // The evidence for the claim, asserted rather than assumed. Settings really does export
        // and really does delete coach history; what it has never had is an account-level wipe.
        settings.Should().Contain("ExportData", "the export alternative depends on this existing");
        settings.Should().Contain("ConfirmDeleteCoachHistory");
        settings.Should().NotContain("DeleteAccount");
        settings.Should().NotContain("StartClean");

        var limitation = CoachLimitations.BulkVocabularyDeletion(412, AsOf);

        limitation.FullScopeSurface.Should().BeNull(
            "there is no screen that performs the whole request, and naming one that cannot is "
            + "worse than naming none");

        limitation.ExportSurface!.Route.Should().Be(CoachRouteName.Settings);
    }

    [Fact]
    public void No_deterministic_copy_claims_a_capability_the_app_lacks()
    {
        var copy = CoachCopyValues().ToList();
        copy.Should().NotBeEmpty();

        var s15 = CoachDeterministicCopy.BulkVocabularyDeletionRefusal
                  + " " + CoachDeterministicCopy.BulkVocabularyDeletionRedirect
                  + " " + CoachDeterministicCopy.BulkVocabularyDeletionExportSurface;

        // The sentence that had to go: "If you really do want to start from nothing, that lives in
        // your settings."
        s15.Should().NotContain("start from nothing");
        s15.Should().NotContain("do it yourself",
            "which implied a self-service whole-vocabulary deletion that does not exist");

        CoachDeterministicCopy.BulkVocabularyDeletionExportSurface.Should().Contain("copy",
            "what Settings really offers is the export");
    }

    [Fact]
    public void The_export_alternative_and_the_export_screen_travel_together()
    {
        var limitation = CoachLimitations.BulkVocabularyDeletion(412, AsOf);

        limitation.Alternatives.Should().Contain(CoachAlternativeCode.ExportBeforeRemoving);
        limitation.ExportSurface.Should().NotBeNull(
            "a link with no alternative explaining it, or an alternative with no link, is half an "
            + "answer either way");
    }

    // ------------------------------------------- B2: side effects are ceilings

    [Fact]
    public void Every_route_discloses_the_most_consequential_thing_it_permits()
    {
        var expected = new Dictionary<CoachRouteName, CoachRouteSideEffect>
        {
            [CoachRouteName.ActivityLog] = CoachRouteSideEffect.None,
            [CoachRouteName.Vocabulary] = CoachRouteSideEffect.EditsLearnerData,

            // Not ChangesSettings. Settings deletes the learner's whole coach conversation
            // history, and an irreversible deletion outranks a preference change.
            [CoachRouteName.Settings] = CoachRouteSideEffect.EditsLearnerData,

            [CoachRouteName.Skills] = CoachRouteSideEffect.EditsLearnerData,
            [CoachRouteName.Writing] = CoachRouteSideEffect.StartsActivity,
            [CoachRouteName.Feedback] = CoachRouteSideEffect.PublishesPublicly
        };

        var checkedRoutes = 0;
        foreach (var (route, ceiling) in expected)
        {
            CoachRouteCatalog.All[route].SideEffect.Should().Be(ceiling);
            checkedRoutes++;
        }

        checkedRoutes.Should().Be(6);

        CoachRouteCatalog.All.Values.Should().NotContain(
            descriptor => descriptor.SideEffect == CoachRouteSideEffect.Unknown,
            "an undisclosed consequence renders as a neutral note, which reads as 'safe'");
    }

    [Fact]
    public void The_settings_page_really_can_delete_learner_data()
    {
        // The evidence for raising the ceiling, so the table is not just asserted against itself.
        SettingsPageSource().Should().Contain("AskDeleteCoachHistory");
    }

    // ------------------------------------------- B3: the facts are true or absent

    [Fact]
    public void A_caller_that_does_not_state_coverage_does_not_get_a_complete_claim()
    {
        CoachLimitations.BulkVocabularyDeletion(412, AsOf).Coverage
            .Should().Be(CoachEvidenceCoverage.Unknown,
                "silence about coverage is not evidence of totality");

        CoachLimitations.BulkVocabularyDeletion(412, AsOf, CoachEvidenceCoverage.CompleteOwnedSet)
            .Coverage.Should().Be(CoachEvidenceCoverage.CompleteOwnedSet,
                "a complete claim is still available — it just has to be made");
    }

    [Fact]
    public void A_count_of_zero_is_absent_rather_than_stated()
    {
        CoachLimitations.BulkVocabularyDeletion(0, AsOf).AffectedCount
            .Should().BeNull("a rendered '0 words' is a fact the server checked; no count is not");

        CoachLimitations.ReviewAnswerDisclosure(0, 0, AsOf, ReviewDay).AffectedCount
            .Should().BeNull();
    }

    [Fact]
    public void A_count_above_zero_is_stated_exactly()
    {
        CoachLimitations.BulkVocabularyDeletion(412, AsOf).AffectedCount.Should().Be(412);
        CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay).AffectedCount.Should().Be(18);
    }

    [Fact]
    public void A_single_day_claim_carries_the_day_it_is_about()
    {
        var limitation = CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);

        limitation.Coverage.Should().Be(CoachEvidenceCoverage.SingleDay);
        limitation.WindowStartDate.Should().Be(ReviewDay);
        limitation.WindowEndDate.Should().Be(ReviewDay,
            "one day is a degenerate window, and stating both bounds lets a renderer show the "
            + "claim rather than ask the learner to trust it");
    }

    [Fact]
    public void Coverage_and_window_dates_cannot_disagree()
    {
        var checkedCases = 0;

        foreach (var limitation in ShippedLimitations())
        {
            if (limitation.Coverage == CoachEvidenceCoverage.SingleDay)
            {
                limitation.WindowStartDate.Should().NotBeNull(
                    "SingleDay with no day is a coverage claim nobody can check");
                limitation.WindowStartDate.Should().Be(limitation.WindowEndDate);
            }

            if (limitation.WindowStartDate is { } start && limitation.WindowEndDate is { } end)
            {
                start.Should().BeOnOrBefore(end);
            }

            checkedCases++;
        }

        checkedCases.Should().Be(2, "both shipped builders were examined");
    }

    [Fact]
    public void Every_timestamp_is_whole_seconds()
    {
        var fractional = AsOf.AddTicks(4_821_593);

        var checkedCases = 0;
        foreach (var limitation in new[]
                 {
                     CoachLimitations.BulkVocabularyDeletion(1, fractional),
                     CoachLimitations.ReviewAnswerDisclosure(2, 1, fractional, ReviewDay)
                 })
        {
            limitation.AsOfUtc!.Value.Ticks.Should().Be(
                limitation.AsOfUtc.Value.Ticks - (limitation.AsOfUtc.Value.Ticks % TimeSpan.TicksPerSecond));
            limitation.AsOfUtc.Value.Kind.Should().Be(DateTimeKind.Utc);
            limitation.AsOfUtc.Value.Should().BeOnOrBefore(fractional, "truncated, never rounded up");
            checkedCases++;
        }

        checkedCases.Should().Be(2);
    }

    // ------------------------------------------- B4: parameter values are checked

    [Theory]
    [InlineData("사과?answer=apple")]
    [InlineData("12?answer=apple")]
    [InlineData("12#answer")]
    [InlineData("12&x=1")]
    [InlineData("12/34")]
    [InlineData("12 34")]
    [InlineData(" 12")]
    [InlineData("12 ")]
    [InlineData("12\t")]
    [InlineData("12\n")]
    [InlineData("12\u0000")]
    [InlineData("-12")]
    [InlineData("+12")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("사과")]
    [InlineData("abc")]
    [InlineData("../settings")]
    [InlineData("99999999999999999999")]
    public void An_identifier_that_is_not_a_positive_integer_never_reaches_the_wire(string smuggled)
    {
        var keys = new[]
        {
            CoachRouteParameterKey.VocabularyWordId,
            CoachRouteParameterKey.ResourceId
        };

        var checkedKeys = 0;
        foreach (var key in keys)
        {
            var destination = CoachRouteCatalog.Build(
                CoachRouteName.Vocabulary,
                [new CoachRouteParameterDto(key, smuggled)]);

            destination!.Parameters.Should().BeEmpty($"{key} accepts positive integers only");

            // The stronger assertion: not merely absent from the list, absent from the bytes.
            JsonSerializer.Serialize(destination, WireJson.Client)
                .Should().NotContain(smuggled.Trim());

            checkedKeys++;
        }

        checkedKeys.Should().Be(2);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("412")]
    [InlineData("9223372036854775807")]
    public void A_real_identifier_is_carried(string identifier)
    {
        CoachRouteCatalog.Build(
                CoachRouteName.Vocabulary,
                [new CoachRouteParameterDto(CoachRouteParameterKey.VocabularyWordId, identifier)])!
            .Parameters.Should().ContainSingle()
            .Which.Value.Should().Be(identifier);
    }

    [Theory]
    [InlineData("2026-08-22T00:00:00Z")]
    [InlineData("2026-08-22 00:00")]
    [InlineData("2026-8-2")]
    [InlineData("22-08-2026")]
    [InlineData("2026-08-22?answer=apple")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("오늘")]
    [InlineData(" 2026-08-22")]
    [InlineData("2026-08-22 ")]
    public void A_plan_date_that_is_not_an_ISO_calendar_day_never_reaches_the_wire(string smuggled)
    {
        var destination = CoachRouteCatalog.Build(
            CoachRouteName.ActivityLog,
            [new CoachRouteParameterDto(CoachRouteParameterKey.PlanDate, smuggled)]);

        destination!.Parameters.Should().BeEmpty();
        JsonSerializer.Serialize(destination, WireJson.Client).Should().NotContain(smuggled.Trim());
    }

    [Fact]
    public void A_real_plan_date_is_carried()
    {
        CoachRouteCatalog.Build(
                CoachRouteName.ActivityLog,
                [new CoachRouteParameterDto(CoachRouteParameterKey.PlanDate, "2026-08-22")])!
            .Parameters.Should().ContainSingle()
            .Which.Value.Should().Be("2026-08-22");
    }

    [Fact]
    public void An_unvalidated_key_carries_nothing()
    {
        // Refuse-by-default. A seventh parameter key added without a rule ships carrying no value
        // rather than carrying an unchecked one.
        CoachRouteCatalog.IsWellFormed(
            new CoachRouteParameterDto(CoachRouteParameterKey.Unknown, "12")).Should().BeFalse();

        var covered = Enum.GetValues<CoachRouteParameterKey>()
            .Where(key => key != CoachRouteParameterKey.Unknown)
            .Count(key => CoachRouteCatalog.IsWellFormed(new CoachRouteParameterDto(key, "12"))
                || CoachRouteCatalog.IsWellFormed(new CoachRouteParameterDto(key, "2026-08-22")));

        covered.Should().Be(4, "every non-Unknown key has a stated shape");
    }

    // ------------------------------- follow-up 1: alternatives that really exist

    [Fact]
    public void Every_offered_alternative_maps_to_a_capability_in_the_shipped_app()
    {
        var limitation = CoachLimitations.BulkVocabularyDeletion(412, AsOf);

        limitation.Alternatives.Should().BeEquivalentTo(
            [
                CoachAlternativeCode.ExportBeforeRemoving,
                CoachAlternativeCode.RemoveOneListAtATime,
                CoachAlternativeCode.StartAFreshList
            ],
            options => options.WithStrictOrdering());

        // The proof, one capability at a time, read out of the shipped UI rather than asserted.
        var proofs = new (CoachAlternativeCode Code, string File, string Evidence)[]
        {
            (CoachAlternativeCode.ExportBeforeRemoving, "Pages/Settings.razor", "ExportData"),
            (CoachAlternativeCode.RemoveOneListAtATime, "Pages/Vocabulary.razor", "RequestBulkDelete"),
            (CoachAlternativeCode.StartAFreshList, "Pages/ResourceAdd.razor", "@page \"/resources/add\"")
        };

        var proven = 0;
        foreach (var (code, file, evidence) in proofs)
        {
            limitation.Alternatives.Should().Contain(code);
            UiSource(file).Should().Contain(evidence,
                $"{code} is offered to learners, so the control has to exist");
            proven++;
        }

        proven.Should().Be(3, "every offered alternative was proven, not just counted");
    }

    [Fact]
    public void The_two_alternatives_the_app_cannot_do_are_gone_from_the_contract()
    {
        var names = Enum.GetNames<CoachAlternativeCode>();

        names.Should().NotContain("ArchiveInsteadOfDelete",
            "only SkillProfile carries IsArchived; vocabulary has no archive at all");
        names.Should().NotContain("PauseReviewsInstead",
            "nothing in the app pauses a review schedule");

        // The gaps are left rather than reused, so a value from an in-flight build decodes as
        // Unknown and is dropped rather than silently becoming a different offer.
        Enum.IsDefined((CoachAlternativeCode)1).Should().BeFalse();
        Enum.IsDefined((CoachAlternativeCode)4).Should().BeFalse();
        ((int)CoachAlternativeCode.RemoveOneListAtATime).Should().Be(2, "survivors keep their ordinals");
        ((int)CoachAlternativeCode.UseHintLadder).Should().Be(7);
    }

    // ------------------------------- follow-up 2: the ladder is monotonic in form

    [Fact]
    public void The_ladder_never_reveals_the_form_earlier_than_the_rung_before_it()
    {
        var ladder = CoachLimitations.HintLadder;

        ladder.Select(rung => rung.Rung).Should().Equal([1, 2, 3]);
        ladder.Select(rung => rung.Kind).Should().Equal(
            [CoachHintKind.Category, CoachHintKind.Cloze, CoachHintKind.FormCue],
            "in Korean an initial character plus a length is very nearly the answer for a two- or "
            + "three-block target, so the form cue is last");

        ladder.Select(rung => CoachLimitations.FormDisclosureRank(rung.Kind))
            .Should().BeInAscendingOrder();

        // Non-vacuity: the ranking must actually distinguish the three, or ascending order is free.
        ladder.Select(rung => CoachLimitations.FormDisclosureRank(rung.Kind))
            .Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void An_unrecognised_rung_sorts_last_so_the_check_cannot_be_defeated()
    {
        CoachLimitations.FormDisclosureRank((CoachHintKind)99)
            .Should().Be(int.MaxValue, "an unknown rung discloses an unknown amount");

        CoachLimitations.FormDisclosureRank(CoachHintKind.Unknown).Should().Be(int.MaxValue);
    }

    [Fact]
    public void No_rung_and_no_limitation_member_can_carry_an_answer()
    {
        typeof(CoachHintRungDto).GetProperties()
            .Should().OnlyContain(property =>
                property.PropertyType == typeof(int) || property.PropertyType == typeof(CoachHintKind));

        var json = JsonSerializer.Serialize(
            CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay), WireJson.Client);

        json.Should().NotContain("answer", "no member on this shape can hold one");
        json.Should().NotContain("term");
        json.Should().NotContain("gloss");
    }

    // ------------------- follow-up 3: repair copy stays out of a non-English answer

    [Theory]
    [InlineData("ko")]
    [InlineData("ko-KR")]
    [InlineData("ja")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("english")]
    public void Repair_is_held_back_when_the_copy_is_not_in_the_learners_language(string displayTag)
    {
        // The API's repair sentences are English constants and the server has no localisation —
        // learner-visible copy is the client's resx. Substituting into a Korean answer would put
        // English in front of the learner, which is the grounding layer creating the defect it
        // exists to remove. The finding is still recorded; only the rewrite is withheld.
        CoachTurnGroundingEvaluator.SuppressRepairForLanguage(
            CoachGroundingStage.Repair, AnswerIn(displayTag)).Should().BeTrue();

        CoachTurnGroundingEvaluator.SuppressRepairForLanguage(
            CoachGroundingStage.Enforce, AnswerIn(displayTag)).Should().BeTrue();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("EN")]
    public void Repair_proceeds_when_the_copy_and_the_answer_agree(string displayTag)
    {
        CoachTurnGroundingEvaluator.SuppressRepairForLanguage(
            CoachGroundingStage.Repair, AnswerIn(displayTag)).Should().BeFalse();
    }

    [Theory]
    [InlineData(CoachGroundingStage.Off)]
    [InlineData(CoachGroundingStage.Observe)]
    public void The_language_guard_does_not_touch_the_rungs_that_never_substitute(
        CoachGroundingStage stage)
    {
        CoachTurnGroundingEvaluator.SuppressRepairForLanguage(stage, AnswerIn("ko"))
            .Should().BeFalse("there is nothing to hold back below Repair");
    }

    // ---------------------------------------------------------------- helpers

    private static CoachAnswerDto AnswerIn(string displayTag) => new()
    {
        Topic = CoachAnswerTopic.Vocabulary,
        Blocks = [],
        PlainText = string.Empty,
        TargetLanguageTag = "ko",
        DisplayLanguageTag = displayTag
    };

    private static IEnumerable<CoachLimitationDto> ShippedLimitations()
    {
        yield return CoachLimitations.BulkVocabularyDeletion(412, AsOf);
        yield return CoachLimitations.ReviewAnswerDisclosure(18, 6, AsOf, ReviewDay);
    }

    private static IEnumerable<string> CoachCopyValues() =>
        typeof(CoachDeterministicCopy)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static IReadOnlyCollection<string> ShippedPageRoutes()
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(UiRoot(), "*.razor", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, "^@page \"([^\"]+)\"");
                if (match.Success)
                {
                    routes.Add(match.Groups[1].Value);
                }
            }
        }

        return routes;
    }

    private static string SettingsPageSource() => UiSource("Pages/Settings.razor");

    private static string UiSource(string relative) =>
        File.ReadAllText(Path.Combine(UiRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string UiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return Path.Combine(directory!.FullName, "src", "SentenceStudio.UI");
    }
}
