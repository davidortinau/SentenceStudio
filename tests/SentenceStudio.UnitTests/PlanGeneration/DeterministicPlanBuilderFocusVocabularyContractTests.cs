using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.PlanGeneration;

public sealed class DeterministicPlanBuilderFocusVocabularyContractTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderFocusVocabularyContractTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task FocusVocabulary_BuilderProducesFocusIdsEqualToPreviewIds()
    {
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(id: "focus-preview-resource", title: "Focus Preview Resource", vocabWordCount: 8);
        _fixture.SeedSkill(id: "focus-preview-skill");

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.25f,
                nextReviewDate: DateTime.UtcNow.Date.AddDays(-1),
                resourceId: resource.Id);
        }

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        var focusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            plan!,
            "the plan-level focus set is the source of truth for preview words");
        var previewIds = FocusVocabularyContractTestHelpers.GetPreviewWordIds(plan!.Narrative);

        focusIds.Should().Equal(previewIds,
            "PreviewWords must be a projection of the exact ordered focus vocabulary set, not an independent sample");
    }

    [Fact]
    public async Task FocusVocabulary_BuilderOrdersFocusSelectionByNextReviewDateThenId()
    {
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(id: "focus-order-resource", title: "Focus Order Resource", vocabWordCount: 0);
        _fixture.SeedSkill(id: "focus-order-skill");

        var today = DateTime.UtcNow.Date;
        var schedule = new[]
        {
            (WordId: "focus-order-c", ProgressId: "progress-order-01", NextReviewDate: today.AddDays(-5)),
            (WordId: "focus-order-a", ProgressId: "progress-order-99", NextReviewDate: today.AddDays(-5)),
            (WordId: "focus-order-e", ProgressId: "progress-order-02", NextReviewDate: today.AddDays(-1)),
            (WordId: "focus-order-b", ProgressId: "progress-order-50", NextReviewDate: today.AddDays(-3)),
            (WordId: "focus-order-d", ProgressId: "progress-order-98", NextReviewDate: today.AddDays(-1)),
        };

        SeedVocabularyProgressWithIds(resource.Id, schedule);

        var expectedOrder = schedule
            .OrderBy(item => item.NextReviewDate)
            .ThenBy(item => item.WordId, StringComparer.Ordinal)
            .Select(item => item.WordId)
            .ToList();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        var focusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            plan!,
            "focus selection must be deterministic and stable across plan rebuilds");
        focusIds.Should().Equal(expectedOrder,
            "Phase 1 selection policy is NextReviewDate ascending, then VocabularyWordId ascending");
    }

    [Fact]
    public async Task FocusVocabulary_BuilderProducesIdenticalFocusOrderAcrossRuns()
    {
        _fixture.SeedUserProfile(40);
        var resource = _fixture.SeedResource(id: "focus-stability-resource", title: "Focus Stability Resource", vocabWordCount: 12);
        _fixture.SeedSkill(id: "focus-stability-skill");

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        for (var i = 0; i < wordIds.Count; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.25f,
                nextReviewDate: DateTime.UtcNow.Date.AddDays(-1 - (i % 3)),
                resourceId: resource.Id);
        }

        var first = await _fixture.CreateBuilder().BuildPlanAsync();
        var second = await _fixture.CreateBuilder().BuildPlanAsync();

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        var firstFocusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            first!,
            "same fixture should produce the same focus vocabulary order every time");
        var secondFocusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            second!,
            "same fixture should produce the same focus vocabulary order every time");

        secondFocusIds.Should().Equal(firstFocusIds,
            "the focus set cannot reroll between plan loads for the same user-local day");
    }

    [Fact]
    public async Task FocusVocabulary_BuilderAssignsFocusIdsToVocabularyAlignedActivities()
    {
        var plan = await BuildPlanWithFullVocabularySurfaceAsync();
        var planFocusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            plan,
            "vocabulary-aligned activities consume the plan-level focus set");

        var alignedActivities = plan.Activities
            .Where(activity => FocusVocabularyContractTestHelpers.IsVocabularyAlignedActivity(activity.ActivityType))
            .ToList();

        alignedActivities.Should().NotBeEmpty("the fixture should produce at least VocabularyReview and VocabularyGame");
        alignedActivities.Should().Contain(activity => activity.ActivityType == "VocabularyReview");
        alignedActivities.Should().Contain(activity => activity.ActivityType == "VocabularyGame");

        foreach (var activity in alignedActivities)
        {
            var activityFocusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
                activity,
                $"{activity.ActivityType} must receive the same focus IDs the preview shows");
            activityFocusIds.Should().Equal(planFocusIds,
                "{0} is vocabulary-aligned and must not reroll its own vocabulary subset", activity.ActivityType);
        }
    }

    [Fact]
    public async Task FocusVocabulary_BuilderDoesNotAssignFocusIdsToNonVocabularyActivities()
    {
        var plan = await BuildPlanWithFullVocabularySurfaceAsync();

        var nonVocabularyActivities = plan.Activities
            .Where(activity => FocusVocabularyContractTestHelpers.IsNonVocabularyActivity(activity.ActivityType))
            .ToList();

        nonVocabularyActivities.Should().NotBeEmpty("the fixture should produce at least one incidental-vocabulary activity");
        foreach (var activity in nonVocabularyActivities)
        {
            var activityFocusIds = FocusVocabularyContractTestHelpers.GetOptionalFocusVocabularyIds(activity);
            activityFocusIds.Should().BeEmpty(
                "{0} is incidental-vocabulary tolerant and should not carry the focus vocabulary contract", activity.ActivityType);
        }
    }

    [Fact]
    public async Task FocusVocabulary_MinimumGateOmitsFocusAndVocabularyReview_WhenFewerThanFiveDueWords()
    {
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(id: "focus-min-resource", title: "Focus Minimum Resource", vocabWordCount: 4);
        _fixture.SeedSkill(id: "focus-min-skill");

        foreach (var wordId in _fixture.GetResourceVocabularyWordIds(resource.Id))
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.25f,
                nextReviewDate: DateTime.UtcNow.Date.AddDays(-1),
                resourceId: resource.Id);
        }

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().BeNull("Phase 1 keeps the existing min-5 review gate");
        FocusVocabularyContractTestHelpers.GetOptionalFocusVocabularyIds(plan).Should().BeEmpty(
            "no focus set should be created when there are fewer than five due words");
    }

    [Fact]
    public async Task FocusVocabulary_MaximumGateCapsFocusSetAtTwenty_WhenMoreThanTwentyDueWords()
    {
        _fixture.SeedUserProfile(40);
        var resource = _fixture.SeedResource(id: "focus-max-resource", title: "Focus Maximum Resource", vocabWordCount: 25);
        _fixture.SeedSkill(id: "focus-max-skill");

        foreach (var wordId in _fixture.GetResourceVocabularyWordIds(resource.Id))
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.25f,
                nextReviewDate: DateTime.UtcNow.Date.AddDays(-1),
                resourceId: resource.Id);
        }

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();
        var focusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            plan,
            "the focus set should apply the same cap as the review block");
        focusIds.Should().HaveCount(20, "Phase 1 caps focus vocabulary at 20 words");
    }

    private void SeedVocabularyProgressWithIds(
        string resourceId,
        IEnumerable<(string WordId, string ProgressId, DateTime NextReviewDate)> schedule)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;

        foreach (var item in schedule)
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = item.WordId,
                TargetLanguageTerm = $"target-{item.WordId}",
                NativeLanguageTerm = $"native-{item.WordId}",
                Language = "Korean",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = $"mapping-{item.WordId}",
                ResourceId = resourceId,
                VocabularyWordId = item.WordId,
            });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = item.ProgressId,
                VocabularyWordId = item.WordId,
                UserId = PlanGenerationTestFixture.TestUserId,
                MasteryScore = 0.25f,
                ProductionInStreak = 0,
                CurrentStreak = 0,
                TotalAttempts = 5,
                CorrectAttempts = 2,
                NextReviewDate = item.NextReviewDate,
                ReviewInterval = 1,
                EaseFactor = 2.5f,
                FirstSeenAt = now.AddDays(-7),
                LastPracticedAt = now.AddDays(-1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = $"context-{item.WordId}",
                VocabularyProgressId = item.ProgressId,
                LearningResourceId = resourceId,
                Activity = "VocabularyQuiz",
                InputMode = "MultipleChoice",
                WasCorrect = true,
                LearnedAt = now.AddDays(-3),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.SaveChanges();
    }

    private async Task<PlanSkeleton> BuildPlanWithFullVocabularySurfaceAsync()
    {
        _fixture.SeedUserProfile(40);
        var resource = _fixture.SeedResource(
            id: "focus-full-surface-resource",
            title: "Focus Full Surface Resource",
            mediaType: "Podcast",
            transcript: "Transcript for focus vocabulary testing.",
            vocabWordCount: 22);
        _fixture.SeedSkill(id: "focus-full-surface-skill");
        _fixture.SeedCompletion(
            DateTime.UtcNow.Date.AddDays(-2),
            "Reading",
            resourceId: resource.Id,
            skillId: "focus-full-surface-skill");

        foreach (var wordId in _fixture.GetResourceVocabularyWordIds(resource.Id))
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.25f,
                nextReviewDate: DateTime.UtcNow.Date.AddDays(-1),
                resourceId: resource.Id);
        }

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();
        plan.Should().NotBeNull("the seeded resource, skill, and due vocabulary should produce a plan");
        return plan!;
    }
}
