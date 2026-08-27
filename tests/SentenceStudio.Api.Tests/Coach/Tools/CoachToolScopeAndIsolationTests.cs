using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Proves the read-only tools resolve the trusted user first, keep learners
/// apart, and fail with a typed error instead of an empty answer.
/// </summary>
public class CoachToolScopeAndIsolationTests
{
    [Fact]
    public async Task Profile_tool_fails_closed_without_a_user_scope()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.Scope.CurrentUserProfileId = null;

        var act = () => fixture.ProfileTool.GetAsync();

        var failure = (await act.Should().ThrowAsync<CoachToolException>()).Which;
        failure.Kind.Should().Be(CoachToolFailureKind.Unauthorized);
        failure.Code.Should().Be("unauthorized");
    }

    [Fact]
    public async Task Every_tool_fails_closed_without_a_user_scope()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.Scope.CurrentUserProfileId = null;

        var calls = new List<Func<Task>>
        {
            () => fixture.ProfileTool.GetAsync(),
            () => fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays),
            () => fixture.VocabularyTool.GetAsync(),
            () => fixture.ResourceTool.GetAsync()
        };

        foreach (var call in calls)
        {
            var failure = (await call.Should().ThrowAsync<CoachToolException>()).Which;
            failure.Kind.Should().Be(CoachToolFailureKind.Unauthorized);
        }
    }

    [Fact]
    public async Task No_query_runs_before_the_scope_check_fails()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedResource(CoachToolTestFixture.UserA);
        fixture.Scope.CurrentUserProfileId = null;
        fixture.Commands.Reset();

        var calls = new List<Func<Task>>
        {
            () => fixture.ProfileTool.GetAsync(),
            () => fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays),
            () => fixture.VocabularyTool.GetAsync(),
            () => fixture.ResourceTool.GetAsync()
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<CoachToolException>();
        }

        fixture.Commands.CommandCount.Should().Be(0, "a tool must resolve the user scope before it reads data");
    }

    [Fact]
    public async Task Profile_tool_reads_only_the_scoped_learner()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA, targetLanguage: "Korean", preferredMinutes: 15);
        fixture.SeedProfile(CoachToolTestFixture.UserB, targetLanguage: "Japanese", preferredMinutes: 45,
            targetLanguages: "Japanese");

        var summary = await fixture.ProfileTool.GetAsync();

        summary.TargetLanguage.Should().Be("Korean");
        summary.PreferredSessionMinutes.Should().Be(15);
        summary.TargetLanguages.Should().NotContain("Japanese");
    }

    [Fact]
    public async Task Profile_tool_fails_when_the_learner_has_no_settings()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserB);

        var act = () => fixture.ProfileTool.GetAsync();

        (await act.Should().ThrowAsync<CoachToolException>()).Which
            .Kind.Should().Be(CoachToolFailureKind.ProfileMissing);
    }

    [Fact]
    public async Task Practice_balance_counts_only_the_scoped_learner()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", minutesSpent: 12, daysAgo: 1);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Writing", minutesSpent: 8, daysAgo: 2);
        fixture.SeedCompletion(CoachToolTestFixture.UserB, "Reading", minutesSpent: 999, daysAgo: 1);
        fixture.SeedActivity(CoachToolTestFixture.UserA, daysAgo: 1);
        fixture.SeedActivity(CoachToolTestFixture.UserB, daysAgo: 1);

        var balance = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays);

        balance.TotalMinutes.Should().Be(20);
        balance.InputMinutes.Should().Be(12);
        balance.OutputMinutes.Should().Be(8);
        balance.AttemptCount.Should().Be(1);
        balance.WindowDays.Should().Be(7);
        balance.WindowEndDate.Should().Be(fixture.Today);
        balance.WindowStartDate.Should().Be(fixture.Today.AddDays(-6));
    }

    [Fact]
    public async Task Practice_balance_ignores_work_outside_the_window()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", minutesSpent: 10, daysAgo: 3);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", minutesSpent: 40, daysAgo: 20);

        var week = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays);
        var month = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.ThirtyDays);

        week.TotalMinutes.Should().Be(10);
        month.TotalMinutes.Should().Be(50);
    }

    [Theory]
    [InlineData(CoachPracticeWindow.SevenDays, 7)]
    [InlineData(CoachPracticeWindow.FourteenDays, 14)]
    [InlineData(CoachPracticeWindow.ThirtyDays, 30)]
    public async Task Practice_balance_supports_only_the_three_windows(CoachPracticeWindow window, int expectedDays)
    {
        using var fixture = new CoachToolTestFixture();

        var balance = await fixture.BalanceTool.GetAsync(window);

        balance.WindowDays.Should().Be(expectedDays);
        CoachPracticeWindows.AllowedDays.Should().Equal(7, 14, 30);
    }

    [Fact]
    public async Task Practice_balance_refuses_a_window_that_is_not_defined()
    {
        using var fixture = new CoachToolTestFixture();

        var act = () => fixture.BalanceTool.GetAsync((CoachPracticeWindow)77);

        (await act.Should().ThrowAsync<CoachToolException>()).Which
            .Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
    }

    [Fact]
    public async Task Vocabulary_summary_counts_only_the_scoped_learner()
    {
        using var fixture = new CoachToolTestFixture();
        var mine = fixture.SeedWord("사과", "apple", tags: "food");
        var theirs = fixture.SeedWord("자동차", "car", tags: "travel");

        fixture.SeedProgress(CoachToolTestFixture.UserA, mine.Id, nextReviewDate: fixture.Now.AddDays(-1));
        fixture.SeedProgress(CoachToolTestFixture.UserB, theirs.Id, nextReviewDate: fixture.Now.AddDays(-1));

        var summary = await fixture.VocabularyTool.GetAsync();

        summary.TrackedWordCount.Should().Be(1);
        summary.DueNowCount.Should().Be(1);
        summary.CategoryTags.Should().ContainSingle().Which.Tag.Should().Be("food");
    }

    [Fact]
    public async Task Vocabulary_summary_reports_counts_bands_and_rates()
    {
        using var fixture = new CoachToolTestFixture();
        var dueWord = fixture.SeedWord("사과", "apple", tags: "food,fruit");
        var soonWord = fixture.SeedWord("바다", "sea", tags: "nature");
        var freshWord = fixture.SeedWord("하늘", "sky");

        fixture.SeedProgress(CoachToolTestFixture.UserA, dueWord.Id,
            masteryScore: 0.3f, totalAttempts: 10, correctAttempts: 6,
            nextReviewDate: fixture.Now.AddDays(-2));
        fixture.SeedProgress(CoachToolTestFixture.UserA, soonWord.Id,
            masteryScore: 0.9f, totalAttempts: 10, correctAttempts: 10, productionInStreak: 3,
            nextReviewDate: fixture.Now.AddDays(3));
        fixture.SeedProgress(CoachToolTestFixture.UserA, freshWord.Id,
            masteryScore: 0f, totalAttempts: 0, correctAttempts: 0);

        var summary = await fixture.VocabularyTool.GetAsync();

        summary.TrackedWordCount.Should().Be(3);
        summary.DueNowCount.Should().Be(1);
        summary.DueThisWeekCount.Should().Be(1);
        summary.NeverPracticedCount.Should().Be(1);
        summary.LapseRate.Should().BeApproximately(0.2, 0.001);
        summary.AverageMasteryScore.Should().BeApproximately(0.4, 0.001);
        summary.Bands.Sum(b => b.Count).Should().Be(3);
        summary.Bands.Should().Contain(b => b.Band == "Known");
        summary.Bands.Should().Contain(b => b.Band == "Unknown");
        summary.CategoryTags.Select(t => t.Tag).Should().BeEquivalentTo("food", "fruit");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    public async Task Vocabulary_summary_refuses_a_tag_count_outside_the_range(int maxTags)
    {
        using var fixture = new CoachToolTestFixture();

        var act = () => fixture.VocabularyTool.GetAsync(maxTags);

        (await act.Should().ThrowAsync<CoachToolException>()).Which
            .Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
    }

    [Fact]
    public async Task Resource_catalog_lists_only_owned_resources()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedResource(CoachToolTestFixture.UserA, title: "Mine");
        fixture.SeedResource(CoachToolTestFixture.UserB, title: "Theirs");

        var catalog = await fixture.ResourceTool.GetAsync();

        catalog.TotalCount.Should().Be(1);
        catalog.Resources.Should().ContainSingle().Which.Title.Should().Be("Mine");
    }

    [Fact]
    public async Task Resource_catalog_reports_capabilities_and_last_use()
    {
        using var fixture = new CoachToolTestFixture();
        var podcast = fixture.SeedResource(CoachToolTestFixture.UserA, title: "Korean podcast",
            mediaType: "Podcast", vocabularyCount: 3);
        fixture.SeedResource(CoachToolTestFixture.UserA, title: "Word list",
            mediaType: "Vocabulary List", transcript: null, tags: null);
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Listening", minutesSpent: 5, daysAgo: 2,
            resourceId: podcast.Id);

        var catalog = await fixture.ResourceTool.GetAsync();

        var entry = catalog.Resources.Single(r => r.ResourceId == podcast.Id);
        entry.HasAudio.Should().BeTrue();
        entry.HasTranscript.Should().BeTrue();
        entry.HasVideo.Should().BeFalse();
        entry.VocabularyCount.Should().Be(3);
        entry.DaysSinceLastUse.Should().Be(2);
        entry.Tags.Should().BeEquivalentTo("travel", "food");

        catalog.Resources.Single(r => r.Title == "Word list").DaysSinceLastUse.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task Resource_catalog_refuses_a_result_count_outside_the_range(int maxResults)
    {
        using var fixture = new CoachToolTestFixture();

        var act = () => fixture.ResourceTool.GetAsync(maxResults);

        (await act.Should().ThrowAsync<CoachToolException>()).Which
            .Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
    }

    [Fact]
    public async Task A_data_failure_becomes_a_typed_failure_not_an_empty_answer()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        await fixture.Db.Database.ExecuteSqlRawAsync("DROP TABLE VocabularyProgress");

        var act = () => fixture.VocabularyTool.GetAsync();

        var failure = (await act.Should().ThrowAsync<CoachToolException>()).Which;
        failure.Kind.Should().Be(CoachToolFailureKind.DataAccess);
        failure.Code.Should().Be("data_access_failure");
        failure.InnerException.Should().NotBeNull();
    }
}
