using FluentAssertions;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Tests for the <c>get_practice_history_summary</c> tool (Wash defect 5 implementation).
/// Covers: identity gate, cross-tenant isolation, date conversion, days-since semantics,
/// empty-state, scope/coverage metadata, and privacy (no vocabulary content in result).
/// </summary>
public class PracticeHistorySummaryToolTests
{
    // ─── A1: Uses same source rows as Practice Log, returns latest across completion/activity ───

    [Fact]
    public async Task Returns_latest_date_from_completions_when_no_activities_exist()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "VocabularyReview", 15, daysAgo: 3);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", 10, daysAgo: 7);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.LastPracticeDate.Should().Be(fixture.Today.AddDays(-3));
        result.DaysSincePractice.Should().Be(3);
    }

    [Fact]
    public async Task Returns_latest_date_from_activities_when_no_completions_exist()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedActivity(CoachToolTestFixture.UserA, daysAgo: 2);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.LastPracticeDate.Should().Be(fixture.Today.AddDays(-2));
        result.DaysSincePractice.Should().Be(2);
    }

    [Fact]
    public async Task Returns_latest_across_both_completions_and_activities()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", 10, daysAgo: 5);
        fixture.SeedActivity(CoachToolTestFixture.UserA, daysAgo: 1);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        // Activity at daysAgo=1 is more recent than completion at daysAgo=5
        result.LastPracticeDate.Should().Be(fixture.Today.AddDays(-1));
        result.DaysSincePractice.Should().Be(1);
    }

    [Fact]
    public async Task Returns_latest_from_completions_when_they_are_more_recent_than_activities()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Listening", 20, daysAgo: 0);
        fixture.SeedActivity(CoachToolTestFixture.UserA, daysAgo: 4);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.LastPracticeDate.Should().Be(fixture.Today);
        result.DaysSincePractice.Should().Be(0);
    }

    // ─── A2: Local date conversion and days-since boundary semantics ───

    [Fact]
    public async Task Days_since_is_zero_when_practiced_today()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "VocabularyReview", 10, daysAgo: 0);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.DaysSincePractice.Should().Be(0);
        result.LastPracticeDate.Should().Be(fixture.Today);
    }

    [Fact]
    public async Task Days_since_is_one_when_practiced_yesterday()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "VocabularyReview", 10, daysAgo: 1);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.DaysSincePractice.Should().Be(1);
    }

    // ─── A2 continued: no-rows state ───

    [Fact]
    public async Task Returns_null_fields_when_no_practice_exists()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.LastPracticeDate.Should().BeNull();
        result.DaysSincePractice.Should().BeNull();
    }

    // ─── A3: Tool result contains no vocabulary/private content; has scope/coverage/as-of metadata ───

    [Fact]
    public async Task Result_scope_has_derived_projection_coverage()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", 10, daysAgo: 2);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.Scope.Coverage.Should().Be(CoachScopeCoverage.DerivedProjection);
        result.Scope.DefinitionCode.Should().Be(CoachScopeDefinition.LatestPracticeSummary);
        result.Scope.Filters.Should().Be(CoachScopeFilters.OwnerScoped);
        result.Scope.AsOfUtc.Should().Be(fixture.Now);
        result.Scope.WithheldCount.Should().Be(0);
        result.Scope.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Result_scope_counts_reflect_empty_state()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.Scope.ReturnedCount.Should().Be(0);
        result.Scope.MatchedCount.Should().Be(0);
        result.Scope.EligiblePopulationCount.Should().Be(0);
    }

    [Fact]
    public async Task Result_does_not_contain_vocabulary_or_transcript_content()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", 10, daysAgo: 1);

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        // The result type has exactly 3 properties: LastPracticeDate, DaysSincePractice, Scope
        // None of them can contain vocabulary or private learner content.
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        json.Should().NotContain("transcript", because: "no private content in tool result");
        json.Should().NotContain("mnemonic", because: "no private content in tool result");
    }

    // ─── A4: Tool exists in frozen registry ───

    [Fact]
    public void Tool_name_is_in_all_registered_list()
    {
        CoachToolNames.AllRegistered.Should().Contain(CoachToolNames.GetPracticeHistorySummary);
    }

    [Fact]
    public void Tool_name_is_in_core_all_list()
    {
        CoachToolNames.All.Should().Contain(CoachToolNames.GetPracticeHistorySummary);
    }

    [Fact]
    public void Tool_name_constant_is_snake_case()
    {
        CoachToolNames.GetPracticeHistorySummary.Should().Be("get_practice_history_summary");
    }

    // ─── A8: Cross-tenant and empty user negative tests ───

    [Fact]
    public async Task Fails_closed_with_empty_user_scope()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "VocabularyReview", 10, daysAgo: 1);
        fixture.Scope.CurrentUserProfileId = null;

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);

        var act = () => tool.GetAsync();
        await act.Should().ThrowAsync<CoachToolException>()
            .Where(e => e.Kind == CoachToolFailureKind.Unauthorized);
    }

    [Fact]
    public async Task Does_not_return_other_tenants_data()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedProfile(CoachToolTestFixture.UserB, email: "other@example.com");
        fixture.SeedCompletion(CoachToolTestFixture.UserB, "Reading", 30, daysAgo: 0);
        // UserA has no practice
        fixture.Scope.CurrentUserProfileId = CoachToolTestFixture.UserA;

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        var result = await tool.GetAsync();

        result.LastPracticeDate.Should().BeNull("UserA has no practice — must not see UserB's data");
        result.DaysSincePractice.Should().BeNull();
    }

    [Fact]
    public async Task No_database_query_before_scope_check()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "VocabularyReview", 10, daysAgo: 1);
        fixture.Scope.CurrentUserProfileId = null;
        fixture.Commands.Reset();

        var tool = new PracticeHistorySummaryTool(fixture.Scope, fixture.History, fixture.Dates);
        await tool.Invoking(t => t.GetAsync()).Should().ThrowAsync<CoachToolException>();

        fixture.Commands.CommandCount.Should().Be(0,
            "scope check must run before any database query");
    }
}
