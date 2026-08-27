using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Characterization of the observable behaviour the persistence-boundary move must not change.
/// </summary>
/// <remarks>
/// <para>
/// The suites next to this file already pin the security properties: the scope gate, the
/// cross-tenant gate, the due-word embargo, the transcript embargo, the output ceilings. What they
/// do not pin is the part a refactor is most likely to move without anyone noticing — the order
/// rows come back in, and the difference between "how many exist" and "how many were returned".
/// </para>
/// <para>
/// Those are exactly the properties that live in a LINQ expression and evaporate when the
/// expression is rewritten somewhere else. A tool that returns the right rows in a different order
/// still passes every isolation test in this directory while telling the learner a different story
/// about their own account. So these tests exist to be written before the move and to keep passing
/// after it, unchanged.
/// </para>
/// </remarks>
public class CoachToolBehaviorCharacterizationTests
{
    // ---------------------------------------------------------------------
    // Ordering
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Resource_catalog_orders_by_recency_of_use_then_title()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var usedYesterday = fixture.SeedResource(user, title: "Zebra podcast");
        var usedLastWeek = fixture.SeedResource(user, title: "Apple podcast");
        fixture.SeedResource(user, title: "Beta unused");
        fixture.SeedResource(user, title: "Alpha unused");

        fixture.SeedCompletion(user, "Listening", 10, daysAgo: 1, resourceId: usedYesterday.Id);
        fixture.SeedCompletion(user, "Listening", 10, daysAgo: 7, resourceId: usedLastWeek.Id);

        var summary = await fixture.ResourceTool.GetAsync();

        // Used resources first, closest use first; never-used resources last, ordinal by title.
        summary.Resources.Select(r => r.Title).Should().Equal(
            "Zebra podcast", "Apple podcast", "Alpha unused", "Beta unused");

        summary.Resources[0].DaysSinceLastUse.Should().Be(1);
        summary.Resources[1].DaysSinceLastUse.Should().Be(7);
        summary.Resources[2].DaysSinceLastUse.Should().BeNull();
        summary.Resources[3].DaysSinceLastUse.Should().BeNull();
    }

    [Fact]
    public async Task Resource_catalog_counts_everything_owned_but_returns_only_the_requested_page()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        for (var i = 0; i < 5; i++)
        {
            fixture.SeedResource(user, title: $"Resource {i}");
        }

        var summary = await fixture.ResourceTool.GetAsync(maxResults: 2);

        summary.TotalCount.Should().Be(5, "the count describes the account, not the page");
        summary.ReturnedCount.Should().Be(2);
        summary.Resources.Should().HaveCount(2);
    }

    [Fact]
    public async Task Learning_resource_list_orders_by_most_recently_updated()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var oldest = fixture.SeedResource(user, title: "Oldest");
        var middle = fixture.SeedResource(user, title: "Middle");
        var newest = fixture.SeedResource(user, title: "Newest");

        fixture.Touch(oldest, updatedDaysAgo: 30);
        fixture.Touch(middle, updatedDaysAgo: 10);
        fixture.Touch(newest, updatedDaysAgo: 1);

        var result = await fixture.LearningResourceListTool.GetAsync();

        result.Resources.Select(r => r.Title).Should().Equal("Newest", "Middle", "Oldest");
        result.TotalCount.Should().Be(3);
        result.ReturnedCount.Should().Be(3);
    }

    [Fact]
    public async Task Learning_resource_list_counts_everything_owned_but_returns_only_the_page()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        for (var i = 0; i < 4; i++)
        {
            fixture.SeedResource(user, title: $"Resource {i}");
        }

        var result = await fixture.LearningResourceListTool.GetAsync(maxResults: 1);

        result.TotalCount.Should().Be(4);
        result.ReturnedCount.Should().Be(1);
    }

    [Fact]
    public async Task Skill_list_orders_by_most_recently_updated_and_counts_the_unarchived_set()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var oldest = fixture.SeedSkill(user, title: "Oldest skill");
        var newest = fixture.SeedSkill(user, title: "Newest skill");
        fixture.SeedSkill(user, title: "Archived skill", archived: true);

        fixture.Touch(oldest, updatedDaysAgo: 20);
        fixture.Touch(newest, updatedDaysAgo: 2);

        var result = await fixture.SkillListTool.GetAsync();

        result.Skills.Select(s => s.Title).Should().Equal("Newest skill", "Oldest skill");
        result.TotalCount.Should().Be(2, "an archived skill is not on the shelf the learner sees");
        result.ReturnedCount.Should().Be(2);
    }

    [Fact]
    public async Task Skill_list_counts_everything_active_but_returns_only_the_page()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        for (var i = 0; i < 4; i++)
        {
            fixture.Touch(fixture.SeedSkill(user, title: $"Skill {i}"), updatedDaysAgo: i + 1);
        }

        var result = await fixture.SkillListTool.GetAsync(maxResults: 2);

        result.TotalCount.Should().Be(4);
        result.ReturnedCount.Should().Be(2);
        result.Skills.Select(s => s.Title).Should().Equal("Skill 0", "Skill 1");
    }

    [Fact]
    public async Task Vocabulary_search_orders_by_mastery_and_counts_the_undue_set()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var low = fixture.SeedWord("낮음", "low");
        var high = fixture.SeedWord("높음", "high");
        var middle = fixture.SeedWord("중간", "middle");

        fixture.SeedProgress(user, low.Id, masteryScore: 0.10f);
        fixture.SeedProgress(user, high.Id, masteryScore: 0.90f);
        fixture.SeedProgress(user, middle.Id, masteryScore: 0.50f);

        var result = await fixture.VocabularySearchTool.SearchAsync();

        result.Words.Select(w => w.TargetTerm).Should().Equal("높음", "중간", "낮음");
        result.TotalMatchCount.Should().Be(3);
        result.ReturnedCount.Should().Be(3);
    }

    [Fact]
    public async Task Vocabulary_search_total_count_excludes_due_words()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var undue = fixture.SeedWord("안전", "safe");
        var due = fixture.SeedWord("만기", "due");

        fixture.SeedProgress(user, undue.Id, masteryScore: 0.4f, nextReviewDate: fixture.Now.AddDays(3));
        fixture.SeedProgress(user, due.Id, masteryScore: 0.9f, nextReviewDate: fixture.Now.AddDays(-1));

        var result = await fixture.VocabularySearchTool.SearchAsync();

        result.TotalMatchCount.Should().Be(1, "a due word is embargoed from the count as well as the page");
        result.Words.Select(w => w.TargetTerm).Should().Equal("안전");
    }

    // ---------------------------------------------------------------------
    // Aggregate shapes
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Profile_summary_counts_words_active_skills_and_resources()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        fixture.SeedProfile(user);
        fixture.SeedProfile(CoachToolTestFixture.UserB);

        fixture.SeedProgress(user, fixture.SeedWord("하나", "one").Id);
        fixture.SeedProgress(user, fixture.SeedWord("둘", "two").Id);
        fixture.SeedProgress(CoachToolTestFixture.UserB, fixture.SeedWord("셋", "three").Id);

        fixture.SeedSkill(user);
        fixture.SeedSkill(user, archived: true);
        fixture.SeedSkill(CoachToolTestFixture.UserB);

        fixture.SeedResource(user);
        fixture.SeedResource(CoachToolTestFixture.UserB);

        var summary = await fixture.CurrentProfileSummaryTool.GetAsync();

        summary.TrackedWordCount.Should().Be(2);
        summary.SkillCount.Should().Be(1, "archived skills are excluded everywhere the learner looks");
        summary.ResourceCount.Should().Be(1);
        summary.TargetLanguages.Should().Equal("Korean", "Spanish");
        summary.DaysSinceStart.Should().Be(100);
    }

    [Fact]
    public async Task Plan_summary_reports_no_plan_when_the_day_has_none()
    {
        using var fixture = new CoachToolTestFixture();
        var summary = await fixture.CurrentPlanSummaryTool.GetAsync();

        summary.HasPlan.Should().BeFalse();
        summary.Strategy.Should().BeNull();
        summary.Items.Should().BeEmpty();
        summary.OverallCompletionPct.Should().Be(0);
        summary.PlanDate.Should().Be(fixture.Today.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task Plan_summary_reports_todays_items_and_completion()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        fixture.SeedPlan(user, strategy: "deterministic");
        fixture.SeedCompletion(user, "Reading", minutesSpent: 10, daysAgo: 0, isCompleted: true);
        fixture.SeedCompletion(user, "Writing", minutesSpent: 0, daysAgo: 0, isCompleted: false);
        fixture.SeedCompletion(user, "Listening", minutesSpent: 5, daysAgo: 1, isCompleted: true);

        var summary = await fixture.CurrentPlanSummaryTool.GetAsync();

        summary.HasPlan.Should().BeTrue();
        summary.Strategy.Should().Be("deterministic");
        summary.Items.Should().HaveCount(2, "yesterday's work is not part of today's plan");
        summary.Items.Select(i => i.ActivityType).Should().BeEquivalentTo(["Reading", "Writing"]);
        summary.OverallCompletionPct.Should().Be(50);
    }

    [Fact]
    public async Task Plan_summary_reads_only_the_scoped_learner()
    {
        using var fixture = new CoachToolTestFixture();

        fixture.SeedPlan(CoachToolTestFixture.UserB, strategy: "llm");
        fixture.SeedCompletion(CoachToolTestFixture.UserB, "Reading", 10, daysAgo: 0);

        var summary = await fixture.CurrentPlanSummaryTool.GetAsync();

        summary.HasPlan.Should().BeFalse();
        summary.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Practice_balance_splits_minutes_by_channel_and_counts_active_days()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        fixture.SeedCompletion(user, "Reading", minutesSpent: 12, daysAgo: 0);
        fixture.SeedCompletion(user, "Writing", minutesSpent: 8, daysAgo: 1);
        fixture.SeedCompletion(user, "VocabularyReview", minutesSpent: 5, daysAgo: 1);
        fixture.SeedCompletion(user, "Reading", minutesSpent: 0, daysAgo: 2, isCompleted: false);
        fixture.SeedActivity(user, daysAgo: 1);
        fixture.SeedActivity(user, daysAgo: 2);

        var balance = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays);

        balance.WindowDays.Should().Be(7);
        balance.InputMinutes.Should().Be(12);
        balance.OutputMinutes.Should().Be(8);
        balance.MixedMinutes.Should().Be(5);
        balance.TotalMinutes.Should().Be(25);
        balance.ActiveDayCount.Should().Be(2, "a day with zero minutes is not an active day");
        balance.AttemptCount.Should().Be(2);

        // Ordered by minutes desc, then activity type ordinal. Rows with no minutes and no
        // completions are dropped so an untouched activity never pads the report.
        balance.ByActivityType.Select(t => t.ActivityType).Should().Equal(
            "Reading", "Writing", "VocabularyReview");
    }

    [Fact]
    public async Task Vocabulary_due_summary_reports_bands_counts_and_tags()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var dueFood = fixture.SeedWord("음식", "food", tags: "food,daily");
        var dueTravel = fixture.SeedWord("여행", "travel", tags: "travel");
        var thisWeek = fixture.SeedWord("주간", "weekly", tags: "misc");
        var untouched = fixture.SeedWord("새것", "new", tags: "misc");

        fixture.SeedProgress(user, dueFood.Id, nextReviewDate: fixture.Now.AddDays(-1));
        fixture.SeedProgress(user, dueTravel.Id, nextReviewDate: fixture.Now.AddHours(-1));
        fixture.SeedProgress(user, thisWeek.Id, nextReviewDate: fixture.Now.AddDays(3));
        fixture.SeedProgress(user, untouched.Id, totalAttempts: 0, correctAttempts: 0);

        var summary = await fixture.VocabularyTool.GetAsync();

        summary.DueNowCount.Should().Be(2);
        summary.DueThisWeekCount.Should().Be(1);
        summary.NeverPracticedCount.Should().Be(1);
        summary.TrackedWordCount.Should().Be(4);

        // Only the due rows contribute tags, ordered by due count then ordinal by tag.
        summary.CategoryTags.Select(t => t.Tag).Should().Equal("daily", "food", "travel");
        summary.CategoryTags.Should().OnlyContain(t => t.DueCount == 1);
    }

    [Fact]
    public async Task Resource_detail_reports_days_since_last_use()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var resource = fixture.SeedResource(user, title: "Grammar drills", vocabularyCount: 2);
        fixture.SeedCompletion(user, "Reading", 10, daysAgo: 4, resourceId: resource.Id);
        fixture.SeedCompletion(user, "Reading", 10, daysAgo: 12, resourceId: resource.Id);

        var detail = await fixture.LearningResourceDetailTool.GetAsync(resource.Id);

        detail.Title.Should().Be("Grammar drills");
        detail.VocabularyCount.Should().Be(2);
        detail.DaysSinceLastUse.Should().Be(4, "the most recent use is the one that matters");
        detail.HasTranscript.Should().BeTrue();
    }

    [Fact]
    public async Task Resource_detail_reports_no_use_when_the_resource_was_never_practised()
    {
        using var fixture = new CoachToolTestFixture();
        var resource = fixture.SeedResource(CoachToolTestFixture.UserA, title: "Untouched");

        var detail = await fixture.LearningResourceDetailTool.GetAsync(resource.Id);

        detail.DaysSinceLastUse.Should().BeNull();
    }

    [Fact]
    public async Task Skill_detail_reports_days_since_created()
    {
        using var fixture = new CoachToolTestFixture();
        var skill = fixture.SeedSkill(CoachToolTestFixture.UserA, title: "Ordering coffee");

        var detail = await fixture.SkillDetailTool.GetAsync(skill.Id);

        detail.Title.Should().Be("Ordering coffee");
        detail.DaysSinceCreated.Should().Be(40);
    }

    [Fact]
    public async Task Word_detail_reports_attempts_and_practice_recency()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        var word = fixture.SeedWord("사과", "apple", tags: "food");
        fixture.SeedProgress(user, word.Id, masteryScore: 0.375f, totalAttempts: 8, correctAttempts: 6);

        var detail = await fixture.VocabularyWordDetailTool.GetAsync(word.Id);

        detail.TargetTerm.Should().Be("사과");
        detail.NativeTerm.Should().Be("apple");
        detail.TotalAttempts.Should().Be(8);
        detail.CorrectAttempts.Should().Be(6);
        detail.MasteryScore.Should().Be(0.375);
        detail.DaysSinceLastPractice.Should().Be(2);
        detail.Tags.Should().Equal("food");
    }

    [Fact]
    public async Task Learner_profile_summary_reports_languages_and_days_since_start()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var summary = await fixture.ProfileTool.GetAsync();

        summary.TargetLanguage.Should().Be("Korean");
        summary.TargetLanguages.Should().Equal("Korean", "Spanish");
        summary.NativeLanguage.Should().Be("English");
        summary.DisplayLanguage.Should().Be("en");
        summary.PreferredSessionMinutes.Should().Be(20);
        summary.TargetLevel.Should().Be("B1");
        summary.DaysSinceStart.Should().Be(100);
    }

    [Fact]
    public async Task Learner_settings_summary_reports_the_settings_the_learner_can_see()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var settings = await fixture.LearnerSettingsSummaryTool.GetAsync();

        settings.TargetLanguage.Should().Be("Korean");
        settings.NativeLanguage.Should().Be("English");
        settings.DisplayLanguage.Should().Be("en");
        settings.PreferredSessionMinutes.Should().Be(20);
        settings.TargetLevel.Should().Be("B1");
    }
}
