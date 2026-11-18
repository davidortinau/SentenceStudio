using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// "I have 45 minutes today. Expand the current plan" keeps today's words and resource (Captain,
/// 2026-11-17); a fresh plan does not; a change of words alone is written; undo puts the words back.
/// </summary>
public sealed class PlanServiceKeepTodaysContentTests : IDisposable
{
    private readonly CoachPlanRevisionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task SeedPlanWithWordsAsync(params string[] words)
    {
        _h.Generator.SetDefault(
            ("VocabularyReview", null, "skill-1", 6, 1),
            ("Reading", "resource-1", "skill-1", 10, 2),
            ("Writing", "resource-1", "skill-1", 10, 3));
        await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());
        using var db = _h.NewDbContext();
        var plan = await db.DailyPlans.SingleAsync();
        plan.FocusVocabularyFacts = PlanFactsSerializer.SerializeFocusVocabularyFacts(words);
        await db.SaveChangesAsync();
        _h.Generator.Requests.Clear();
    }

    [Fact]
    public async Task A_revision_preview_keeps_todays_words_and_resource()
    {
        await SeedPlanWithWordsAsync("w1", "w2");

        await _h.NewService().PreviewPlanAsync(new PlanConstraints { AvailableMinutes = 45 }, focusVocabularyWordIds: null);

        var request = _h.Generator.Requests.Should().ContainSingle().Which;
        request.FocusVocabularyWordIds.Should().Equal("w1", "w2");
        request.PinnedResourceId.Should().Be("resource-1");
    }

    [Fact]
    public async Task A_fresh_plan_preview_keeps_nothing()
    {
        await SeedPlanWithWordsAsync("w1", "w2");

        await _h.NewService().PreviewPlanAsync(constraints: null);

        var request = _h.Generator.Requests.Should().ContainSingle().Which;
        request.FocusVocabularyWordIds.Should().BeNull();
        request.PinnedResourceId.Should().BeNull();
    }

    [Fact]
    public async Task New_focus_words_replace_todays_words()
    {
        await SeedPlanWithWordsAsync("w1", "w2");

        await _h.NewService().PreviewPlanAsync(new PlanConstraints(), focusVocabularyWordIds: ["food-1"]);

        _h.Generator.Requests.Single().FocusVocabularyWordIds.Should().Equal("food-1");
    }

    [Fact]
    public async Task An_empty_word_set_clears_todays_words()
    {
        await SeedPlanWithWordsAsync("w1", "w2");

        await _h.NewService().PreviewPlanAsync(new PlanConstraints(), focusVocabularyWordIds: []);

        _h.Generator.Requests.Single().FocusVocabularyWordIds.Should().BeNull(
            "clearing the focus lets the planner derive the words again");
    }

    [Fact]
    public async Task Undo_restores_no_words_when_the_plan_had_none()
    {
        await SeedPlanWithWordsAsync();
        using (var db = _h.NewDbContext())
        {
            var plan = await db.DailyPlans.SingleAsync();
            plan.FocusVocabularyFacts = null;
            await db.SaveChangesAsync();
        }
        var before = await _h.NewService().GetTodaySnapshotAsync();
        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = new PlanConstraints(),
            ExpectedPlanVersion = before.Version,
            FocusVocabularyWordIds = ["food-1"]
        });

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = applied.Before!,
            ExpectedPlanVersion = applied.AfterPlanVersion,
            OperationKey = "undo-none"
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        (await FocusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_change_of_words_alone_is_written_and_undo_brings_the_old_words_back()
    {
        await SeedPlanWithWordsAsync("w1", "w2");
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = new PlanConstraints(),
            ExpectedPlanVersion = before.Version,
            FocusVocabularyWordIds = ["food-1", "food-2"]
        });

        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied, "the items kept their shape but the words changed");
        (await FocusAsync()).Should().Equal("food-1", "food-2");

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = applied.Before!,
            ExpectedPlanVersion = applied.AfterPlanVersion,
            OperationKey = "undo-words"
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        (await FocusAsync()).Should().Equal("w1", "w2");
    }

    // Phase 5 (Captain, 2026-11-17): a set chosen through Sam is marked so serve-time trimming
    // keeps it, and a revision that keeps today's words keeps the mark.
    [Fact]
    public async Task A_set_chosen_through_sam_is_marked_and_stays_marked_when_kept()
    {
        await SeedPlanWithWordsAsync("w1", "w2");
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var chosen = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = new PlanConstraints(),
            ExpectedPlanVersion = before.Version,
            FocusVocabularyWordIds = ["food-1", "food-2"]
        });
        (await FactsAsync()).Should().Match<string?>(f => PlanFactsSerializer.IsCoachFocus(f));

        await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = new PlanConstraints { AvailableMinutes = 45 },
            ExpectedPlanVersion = chosen.AfterPlanVersion
        });
        (await FactsAsync()).Should().Match<string?>(f => PlanFactsSerializer.IsCoachFocus(f), "keeping today's words keeps their source");
    }

    [Fact]
    public async Task The_planners_own_words_are_not_marked()
    {
        await SeedPlanWithWordsAsync("w1", "w2");

        PlanFactsSerializer.IsCoachFocus(await FactsAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task New_focus_words_keep_todays_resource()
    {
        await SeedPlanWithWordsAsync("w1", "w2");

        await _h.NewService().PreviewPlanAsync(new PlanConstraints(), focusVocabularyWordIds: ["food-1"]);

        _h.Generator.Requests.Single().PinnedResourceId.Should().Be("resource-1",
            "a focus changes the words, not the resource the learner is working through");
    }

    private async Task<string?> FactsAsync()
    {
        using var db = _h.NewDbContext();
        return (await db.DailyPlans.SingleAsync()).FocusVocabularyFacts;
    }

    private async Task<List<string>> FocusAsync()
    {
        using var db = _h.NewDbContext();
        var plan = await db.DailyPlans.SingleAsync();
        return PlanFactsSerializer.DeserializeFocusVocabularyFacts(plan.FocusVocabularyFacts);
    }
}
