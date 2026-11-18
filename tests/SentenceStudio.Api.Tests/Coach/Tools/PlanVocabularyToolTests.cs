using FluentAssertions;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// get_plan_vocabulary lists today's plan words with meaning and mastery when the learner asks
/// (Captain, 2026-11-17: "preview today's vocabulary", "what vocab am I worst at"). A word whose
/// answer is hidden on the learner's screen is left out.
/// </summary>
public class PlanVocabularyToolTests
{
    [Fact]
    public async Task No_plan_says_so()
    {
        using var fixture = new CoachToolTestFixture();

        var result = await fixture.PlanVocabularyTool.GetAsync();

        result.HasPlan.Should().BeFalse();
        result.Words.Should().BeEmpty();
    }

    [Fact]
    public async Task The_plan_words_come_in_plan_order_with_meaning_and_mastery_due_or_not()
    {
        using var fixture = new CoachToolTestFixture();
        var (fish, grape, school) = Words(fixture);
        // 생선 is due right now: the learner asked, so it is named.
        fixture.SeedProgress(CoachToolTestFixture.UserA, fish.Id, masteryScore: 0.6f, nextReviewDate: fixture.Now.AddHours(-1));
        fixture.SeedProgress(CoachToolTestFixture.UserA, grape.Id, masteryScore: 0.1f);
        Plan(fixture, fish.Id, grape.Id, school.Id);
        fixture.Turn.ClientScreen = null;

        var result = await fixture.PlanVocabularyTool.GetAsync();

        result.HasPlan.Should().BeTrue();
        result.Words.Select(w => w.TargetTerm).Should().Equal("생선", "포도", "학교");
        var first = result.Words[0];
        first.NativeTerm.Should().Be("fish");
        first.MasteryPct.Should().Be(60);
        result.Words[2].MasteryPct.Should().BeNull("학교 has never been practised");
        result.Scope.Order.Should().Be(CoachScopeOrder.PriorityAscending);
        fixture.Turn.ListedWords!.Words.Should().HaveCount(3, "the reply shows the list the tool read");
    }

    [Fact]
    public async Task Weakest_first_puts_the_lowest_mastery_first_and_unpractised_words_last()
    {
        using var fixture = new CoachToolTestFixture();
        var (fish, grape, school) = Words(fixture);
        fixture.SeedProgress(CoachToolTestFixture.UserA, fish.Id, masteryScore: 0.6f);
        fixture.SeedProgress(CoachToolTestFixture.UserA, grape.Id, masteryScore: 0.1f);
        Plan(fixture, school.Id, fish.Id, grape.Id);
        fixture.Turn.ClientScreen = null;

        var result = await fixture.PlanVocabularyTool.GetAsync(order: "weakest");

        result.Words.Select(w => w.TargetTerm).Should().Equal("포도", "생선", "학교");
        result.Scope.Order.Should().Be(CoachScopeOrder.MasteryAscending);
        fixture.Turn.ListedWords!.WeakestFirst.Should().BeTrue();
    }

    [Fact]
    public async Task A_word_whose_answer_is_hidden_on_screen_is_left_out_and_counted()
    {
        using var fixture = new CoachToolTestFixture();
        var (fish, grape, _) = Words(fixture);
        Plan(fixture, fish.Id, grape.Id);
        fixture.Turn.ClientScreen = new CoachClientScreenContext
        {
            ScreenId = "/vocab-quiz",
            AnswerHidden = true,
            Items = [new CoachClientScreenItem { Kind = CoachClientScreenItemKinds.VocabularyWord, Id = fish.Id, Label = "fish", Focus = true }]
        };

        var result = await fixture.PlanVocabularyTool.GetAsync();

        result.Words.Select(w => w.WordId).Should().Equal(grape.Id);
        result.WithheldOnScreen.Should().Be(1);
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Another_learners_word_in_the_plan_is_never_returned()
    {
        using var fixture = new CoachToolTestFixture();
        var mine = fixture.SeedWord("포도", "grape");
        var theirs = fixture.SeedWord("비밀", "secret", owner: CoachToolTestFixture.UserB);
        Plan(fixture, mine.Id, theirs.Id);
        fixture.Turn.ClientScreen = null;

        var result = await fixture.PlanVocabularyTool.GetAsync();

        result.Words.Select(w => w.WordId).Should().Equal(mine.Id);
    }

    [Fact]
    public async Task The_plan_summary_names_each_item_and_counts_the_plan_words()
    {
        using var fixture = new CoachToolTestFixture();
        var (fish, grape, _) = Words(fixture);
        Plan(fixture, fish.Id, grape.Id);

        var summary = await fixture.CurrentPlanSummaryTool.GetAsync();

        summary.FocusWordCount.Should().Be(2);
        var review = summary.Items.Should().ContainSingle(i => i.ActivityType == "VocabularyReview").Which;
        review.PlanItemId.Should().NotBeNullOrWhiteSpace();
        review.FocusWordCount.Should().Be(2);
        summary.MinutesPlanned.Should().Be(5);
    }

    private static (VocabularyWord Fish, VocabularyWord Grape, VocabularyWord School) Words(CoachToolTestFixture fixture) =>
        (fixture.SeedWord("생선", "fish"), fixture.SeedWord("포도", "grape"), fixture.SeedWord("학교", "school"));

    private static void Plan(CoachToolTestFixture fixture, params string[] wordIds)
    {
        var plan = fixture.SeedPlan(CoachToolTestFixture.UserA);
        plan.FocusVocabularyFacts = PlanFactsSerializer.SerializeFocusVocabularyFacts(wordIds);
        fixture.Db.SaveChanges();
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "VocabularyReview", minutesSpent: 5, daysAgo: 0, isCompleted: false);
    }
}
