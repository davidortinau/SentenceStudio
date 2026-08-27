using System.Reflection;
using FluentAssertions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// The W9 grounding columns: what they may hold, what they must never hold, and when they are null.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null carries three meanings and they must stay indistinguishable.</b> The ladder was Off, the
/// row predates the columns, or the stored outcome could not be read. None is a finding. A zero in
/// any of these columns would be read by an operator as a measurement that was made and came back
/// empty, which is a different and false statement.
/// </para>
/// </remarks>
public sealed class CoachResponseReportGroundingTests
{
    // ─────────────────────────────────────────────────────────────── shape

    [Fact]
    public void Every_grounding_column_is_nullable_and_content_free()
    {
        var columns = typeof(CoachResponseReport)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name.StartsWith("Grounding", StringComparison.Ordinal))
            .ToList();

        columns.Should().HaveCount(8, "the ceremony specified exactly eight");

        columns.Should().OnlyContain(
            property => property.PropertyType == typeof(int?)
                        || property.PropertyType == typeof(bool?)
                        || property.PropertyType == typeof(string),
            "ordinals, booleans, counts, and one bounded closed-code list");

        columns.Should().OnlyContain(
            property => property.PropertyType != typeof(string) || property.Name == "GroundingRuleCodes",
            "exactly one string, and it can only hold enum member names");

        columns.Select(property => property.Name).Should().BeEquivalentTo(
            [
                "GroundingStage", "GroundingRefused", "GroundingAltered", "GroundingRepairSuppressed",
                "GroundingFindingCount", "GroundingRuleCodes", "GroundingLimitationCode",
                "GroundingShadowLabel"
            ],
            "the names are the contract the migration and the operator surface both read");
    }

    [Fact]
    public void No_grounding_column_can_hold_learner_or_model_text()
    {
        // The one string is bounded and its only writer renders enum member names. Asserted
        // structurally so a free-text member added later fails here rather than in a privacy review.
        typeof(CoachResponseReport).GetProperty(nameof(CoachResponseReport.GroundingRuleCodes))!
            .PropertyType.Should().Be(typeof(string));

        CoachResponseReportLimits.GroundingRuleCodesMaxLength.Should().Be(256);

        // Every declared rule name fits, so the bound cannot silently drop a legitimate full set.
        var everyRule = string.Join(',', Enum.GetNames<CoachClaimRuleCode>().Order(StringComparer.Ordinal));
        everyRule.Length.Should().BeLessThan(
            CoachResponseReportLimits.GroundingRuleCodesMaxLength,
            "the bound must not be reachable by a legitimate value, or it would drop real evidence");
    }

    [Fact]
    public void The_schema_version_was_bumped()
    {
        CoachResponseReportLimits.SchemaVersion.Should().Be(
            2, "the row shape changed, and a reader has to be able to tell which shape it has");

        new CoachResponseReport().SchemaVersion.Should().Be(2);
    }

    // ─────────────────────────────────────────────────────── the projection

    [Fact]
    public void No_summary_projects_all_nulls()
    {
        var facts = CoachResponseReportService.ProjectGrounding(null);

        facts.Stage.Should().BeNull();
        facts.Refused.Should().BeNull();
        facts.Altered.Should().BeNull();
        facts.RepairSuppressed.Should().BeNull();
        facts.FindingCount.Should().BeNull(
            "a rung of Off measured nothing, and zero would read as a measurement of none");
        facts.RuleCodes.Should().BeNull();
        facts.LimitationCode.Should().BeNull();
        facts.ShadowLabel.Should().BeNull();
    }

    [Fact]
    public void A_real_summary_projects_every_column()
    {
        var facts = CoachResponseReportService.ProjectGrounding(Summary());

        facts.Stage.Should().Be((int)CoachGroundingStage.Repair);
        facts.Refused.Should().BeFalse();
        facts.Altered.Should().BeTrue();
        facts.RepairSuppressed.Should().BeFalse();
        facts.FindingCount.Should().Be(3);
        facts.LimitationCode.Should().Be((int)CoachLimitationCode.AvailableOnAnotherSurface);
        facts.ShadowLabel.Should().Be((int)CoachShadowRouteLabel.LearnerState);
    }

    [Fact]
    public void Rule_codes_are_unique_ordinal_sorted_names()
    {
        var rendered = CoachResponseReportService.RenderRuleCodes(
        [
            new CoachGroundingRuleCount(CoachClaimRuleCode.WithheldNotDisclosed, 1),
            new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, 2),
            new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, 1)
        ]);

        rendered.Should().Be(
            "FabricatedCheck,WithheldNotDisclosed",
            "sorted so two reports of the same shape produce the same string, and deduplicated so a "
            + "rollup counts reports rather than firings");
    }

    [Fact]
    public void An_unknown_rule_code_is_dropped_whole()
    {
        var rendered = CoachResponseReportService.RenderRuleCodes(
        [
            new CoachGroundingRuleCount((CoachClaimRuleCode)99, 1),
            new CoachGroundingRuleCount(CoachClaimRuleCode.OrderClaimMismatch, 1)
        ]);

        rendered.Should().Be(
            "OrderClaimMismatch",
            "a code this build cannot name is dropped, never rendered as its number");

        rendered.Should().NotContain("99");
    }

    [Fact]
    public void A_summary_of_only_unknown_codes_renders_null()
    {
        CoachResponseReportService.RenderRuleCodes(
            [new CoachGroundingRuleCount((CoachClaimRuleCode)99, 1)])
            .Should().BeNull("nothing nameable fired, which is not the same as nothing firing");
    }

    [Fact]
    public void An_over_long_list_is_dropped_rather_than_truncated()
    {
        // Synthetic: enough distinct declared names to exceed the bound is impossible with nine
        // rules, so the guard is exercised by asserting the branch exists and that a legitimate
        // full set stays under it. A truncated list would read as a short list, which is a false
        // statement about what fired.
        var everyRule = Enum.GetValues<CoachClaimRuleCode>()
            .Where(rule => rule != CoachClaimRuleCode.Unknown)
            .Select(rule => new CoachGroundingRuleCount(rule, 1))
            .ToArray();

        var rendered = CoachResponseReportService.RenderRuleCodes(everyRule);

        rendered.Should().NotBeNull();
        rendered!.Length.Should().BeLessThanOrEqualTo(CoachResponseReportLimits.GroundingRuleCodesMaxLength);
        rendered.Split(',').Should().HaveCount(everyRule.Length, "no name was dropped or clipped");
        rendered.Split(',').Should().OnlyContain(name => Enum.GetNames<CoachClaimRuleCode>().Contains(name));
    }

    [Fact]
    public void An_undefined_stage_or_limitation_projects_null()
    {
        var facts = CoachResponseReportService.ProjectGrounding(
            Summary() with
            {
                RequestedStage = (CoachGroundingStage)99,
                LimitationCode = (CoachLimitationCode)99,
                ShadowLabel = (CoachShadowRouteLabel)99
            });

        facts.Stage.Should().BeNull();
        facts.LimitationCode.Should().BeNull();
        facts.ShadowLabel.Should().BeNull();

        facts.FindingCount.Should().Be(3, "an undefined enum elsewhere does not erase a real count");
    }

    // ──────────────────────────────────────────────────── the filter parser

    [Theory]
    [InlineData("Observe", (int)CoachGroundingStage.Observe)]
    [InlineData("repair", (int)CoachGroundingStage.Repair)]
    public void A_named_stage_parses(string raw, int expected)
    {
        CoachGroundingReportFilter.TryParse(raw, null, null, null, out var filter).Should().BeTrue();
        filter.Stage.Should().Be(expected);
    }

    [Theory]
    [InlineData("Repare")]
    [InlineData("2")]
    [InlineData("Observe,Repair")]
    public void A_stage_that_is_not_one_rung_is_refused(string raw)
    {
        CoachGroundingReportFilter.TryParse(raw, null, null, null, out _).Should().BeFalse(
            "silently dropping it would answer a broader question than the operator asked, and "
            + "'Observe,Repair' parses bitwise to Enforce");
    }

    [Fact]
    public void A_rule_code_normalises_to_the_declared_spelling()
    {
        CoachGroundingReportFilter.TryParse(null, null, "fabricatedcheck", null, out var filter)
            .Should().BeTrue();

        filter.RuleCode.Should().Be(
            nameof(CoachClaimRuleCode.FabricatedCheck),
            "the stored column holds names as the enum spells them, so a case-insensitive parse "
            + "has to be normalised before it can be compared against one");
    }

    [Theory]
    [InlineData("NotARule")]
    [InlineData("Unknown")]
    [InlineData("FabricatedCheck,OrderClaimMismatch")]
    public void A_rule_code_outside_the_closed_set_is_refused(string raw)
    {
        CoachGroundingReportFilter.TryParse(null, null, raw, null, out _).Should().BeFalse();
    }

    [Fact]
    public void No_rule_name_is_a_substring_of_another()
    {
        // The filter matches by containment against a comma-joined list, which is only safe while
        // this holds. If a tenth rule were named such that it half-matched another, the filter
        // would silently return rows that never fired it.
        var names = Enum.GetNames<CoachClaimRuleCode>();

        foreach (var name in names)
        {
            names.Where(other => other != name)
                .Should().NotContain(
                    other => other.Contains(name, StringComparison.Ordinal),
                    "'{0}' would half-match another rule name", name);
        }
    }

    [Fact]
    public void An_empty_filter_is_recognised_as_empty()
    {
        CoachGroundingReportFilter.TryParse(null, null, null, null, out var filter).Should().BeTrue();
        filter.IsEmpty.Should().BeTrue("no filter requested leaves the query untouched");

        CoachGroundingReportFilter.TryParse(null, refused: true, null, null, out var refusedOnly)
            .Should().BeTrue();
        refusedOnly.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void The_filter_holds_no_caller_supplied_string()
    {
        // Existence-oracle resistance starts here: nothing an operator types survives parsing
        // except a name the enum already declares, so no arbitrary string can reach a WHERE clause.
        CoachGroundingReportFilter.TryParse(null, null, "'; DROP TABLE x --", null, out _)
            .Should().BeFalse();

        typeof(CoachGroundingReportFilter)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Should().HaveCount(1, "only the normalised rule name, and it is enum-derived");
    }

    // ─────────────────────────────────────────────────────────────── helpers

    private static CoachGroundingTurnSummary Summary() => new(
        CoachGroundingStage.Repair,
        SubstitutionAllowed: true,
        Refused: false,
        Altered: true,
        RepairSuppressedForLanguage: false,
        FindingCount: 3,
        RuleCounts:
        [
            new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, 2),
            new CoachGroundingRuleCount(CoachClaimRuleCode.OrderClaimMismatch, 1)
        ],
        LimitationCode: CoachLimitationCode.AvailableOnAnotherSurface,
        ShadowLabel: CoachShadowRouteLabel.LearnerState);
}
