using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FluentAssertions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.PlanGeneration;

namespace SentenceStudio.UnitTests.Integration;

/// <summary>
/// Integration tests that simulate a learner using the app over 5-7 days.
/// Tests the full lifecycle: Plan generation → Activity execution → Progress recording
/// → Next plan reflects progress. Uses real database and real services.
/// </summary>
public class MultiDayLearningJourneyTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;
    private readonly VocabularyProgressService _progressService;
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly DeterministicPlanBuilder _builder;

    public MultiDayLearningJourneyTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile(sessionMinutes: 20);

        var scope = fixture.ServiceProvider.CreateScope();
        _progressRepo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();
        _builder = scope.ServiceProvider.GetRequiredService<DeterministicPlanBuilder>();

        var contextRepo = new VocabularyLearningContextRepository(
            fixture.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<VocabularyLearningContextRepository>());

        _progressService = new VocabularyProgressService(
            _progressRepo,
            contextRepo,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<VocabularyProgressService>(),
            fixture.ServiceProvider);
    }

    public void Dispose() { }

    #region Plan Generation Reflects Progress

    [Fact]
    public async Task Day1_FirstPlan_IncludesNewWordsFromResource()
    {
        // Arrange: fresh resource with vocabulary, no progress
        var resource = _fixture.SeedResource(
            id: "day1-resource", title: "Day 1 Podcast",
            mediaType: "Podcast", transcript: "Korean text", vocabWordCount: 15);
        _fixture.SeedSkill(title: "Listening");

        // Act: generate first plan
        var plan = await _builder.BuildPlanAsync();

        // Assert: plan should exist with activities
        plan.Should().NotBeNull();
        plan.Activities.Should().NotBeEmpty("first plan should have activities");
        plan.PrimaryResource.Should().NotBeNull("should select the available resource");
        plan.PrimaryResource!.Id.Should().Be("day1-resource");
    }

    [Fact]
    public async Task AfterProgress_PlanIncludesVocabReview()
    {
        // Arrange: resource with words that have progress and are due
        var resource = _fixture.SeedResource(
            id: "review-resource", title: "Review Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 10);
        _fixture.SeedSkill(title: "Listening");

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // Simulate: learner practiced these words yesterday, they're due today
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                currentStreak: 2,
                totalAttempts: 3,
                correctAttempts: 2,
                nextReviewDate: DateTime.UtcNow.AddDays(-1), // Due today
                resourceId: resource.Id);
        }

        // Act
        var plan = await _builder.BuildPlanAsync();

        // Assert: plan should include vocabulary review
        plan.Should().NotBeNull();
        plan.VocabularyReview.Should().NotBeNull(
            "10 due words should trigger a vocabulary review block");
        plan.VocabularyReview!.TotalDue.Should().BeGreaterOrEqualTo(10);
    }

    [Fact]
    public async Task KnownWords_ExcludedFromVocabReview()
    {
        // Arrange: mix of known and learning words
        var resource = _fixture.SeedResource(
            id: "mixed-resource", title: "Mixed Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 10);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // 5 learning words (due)
        for (int i = 0; i < 5; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.4f,
                currentStreak: 2,
                productionInStreak: 0,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // 5 known words (due date, but known → excluded)
        for (int i = 5; i < 10; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.92f,
                currentStreak: 8,
                productionInStreak: 4,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var plan = await _builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        if (plan.VocabularyReview != null)
        {
            plan.VocabularyReview.DueWords.Should().AllSatisfy(w =>
            {
                (w.MasteryScore >= 0.85f && w.ProductionInStreak >= 2)
                    .Should().BeFalse("known words must not appear in due words list");
            });
        }
    }

    #endregion

    #region Multi-Day Simulation

    [Fact]
    public async Task SimulateMultipleDays_MasteryProgressesCorrectly()
    {
        // Arrange: set up a resource with 8 words
        var resource = _fixture.SeedResource(
            id: "multiday-res", title: "Multi-Day Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 8);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // === Day 1: First encounter — learner sees words for the first time ===
        // Record 2 correct MC attempts per word
        foreach (var wordId in wordIds)
        {
            for (int i = 0; i < 2; i++)
            {
                await _progressService.RecordAttemptAsync(new VocabularyAttempt
                {
                    VocabularyWordId = wordId,
                    UserId = PlanGenerationTestFixture.TestUserId,
                    Activity = "VocabularyQuiz",
                    InputMode = "MultipleChoice",
                    WasCorrect = true,
                    LearningResourceId = resource.Id,
                    ContextType = "Isolated",
                    ResponseTimeMs = 2000
                });
            }
        }

        // After Day 1: all words at ~0.286 mastery (EffStreak=2/7)
        foreach (var wordId in wordIds)
        {
            var p = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
            p.MasteryScore.Should().BeApproximately(2.0f / 7.0f, 0.01f);
            p.IsKnown.Should().BeFalse();
            p.Status.Should().Be(LearningStatus.Learning);
        }

        // === Day 2: More MC practice, some words get a wrong answer ===
        // First 4 words: 2 more correct MC (total streak 4, mastery ≈ 0.57)
        for (int w = 0; w < 4; w++)
        {
            for (int i = 0; i < 2; i++)
            {
                await _progressService.RecordAttemptAsync(new VocabularyAttempt
                {
                    VocabularyWordId = wordIds[w],
                    UserId = PlanGenerationTestFixture.TestUserId,
                    Activity = "VocabularyQuiz",
                    InputMode = "MultipleChoice",
                    WasCorrect = true,
                    ContextType = "Isolated",
                    ResponseTimeMs = 1500
                });
            }
        }

        // Words 4-5: get one wrong answer (streak reset)
        for (int w = 4; w < 6; w++)
        {
            await _progressService.RecordAttemptAsync(new VocabularyAttempt
            {
                VocabularyWordId = wordIds[w],
                UserId = PlanGenerationTestFixture.TestUserId,
                Activity = "VocabularyQuiz",
                InputMode = "MultipleChoice",
                WasCorrect = false,
                ContextType = "Isolated",
                ResponseTimeMs = 3000
            });
        }

        // Verify Day 2 state
        for (int w = 0; w < 4; w++)
        {
            var p = await _progressService.GetProgressAsync(wordIds[w], PlanGenerationTestFixture.TestUserId);
            p.CurrentStreak.Should().BeApproximately(4.0f, 0.01f, $"word {w} should have 4-streak after 4 correct MC");
            p.MasteryScore.Should().BeApproximately(4.0f / 7.0f, 0.01f);
        }
        for (int w = 4; w < 6; w++)
        {
            var p = await _progressService.GetProgressAsync(wordIds[w], PlanGenerationTestFixture.TestUserId);
            p.CurrentStreak.Should().BeGreaterOrEqualTo(0, $"word {w} should have reduced streak after wrong answer");
            p.CurrentStreak.Should().BeLessThan(2.0f, $"word {w} streak should be much less than before");
        }

        // === Day 3: Top words switch to production mode (Text input) ===
        for (int w = 0; w < 4; w++)
        {
            for (int i = 0; i < 2; i++)
            {
                await _progressService.RecordAttemptAsync(new VocabularyAttempt
                {
                    VocabularyWordId = wordIds[w],
                    UserId = PlanGenerationTestFixture.TestUserId,
                    Activity = "VocabularyQuiz",
                    InputMode = "Text",
                    WasCorrect = true,
                    ContextType = "Isolated",
                    ResponseTimeMs = 3000
                });
            }
        }

        // Words 0-3: streak 6, productionInStreak 2, EffStreak = 6+1 = 7 → mastery 1.0
        for (int w = 0; w < 4; w++)
        {
            var p = await _progressService.GetProgressAsync(wordIds[w], PlanGenerationTestFixture.TestUserId);
            p.CurrentStreak.Should().BeApproximately(6.0f, 0.01f);
            p.ProductionInStreak.Should().Be(2);
            p.MasteryScore.Should().BeGreaterOrEqualTo(0.85f);
            p.IsKnown.Should().BeTrue(
                $"word {w} should be Known: mastery >= 0.85 AND productionInStreak >= 2");
            p.MasteredAt.Should().NotBeNull();
        }

        // Words 6-7 still at Day 1 level
        for (int w = 6; w < 8; w++)
        {
            var p = await _progressService.GetProgressAsync(wordIds[w], PlanGenerationTestFixture.TestUserId);
            p.MasteryScore.Should().BeApproximately(2.0f / 7.0f, 0.01f);
            p.IsKnown.Should().BeFalse();
        }

        // === Verify plan reflects progress: Known words excluded from review ===
        var dueWords = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow.AddDays(1));
        var knownWordIds = wordIds.Take(4).ToHashSet();
        dueWords.Select(w => w.VocabularyWordId)
            .Should().NotContain(id => knownWordIds.Contains(id),
                "known words should not appear in due vocabulary");
    }

    #endregion

    #region Resource Rotation Over Multiple Days

    [Fact]
    public async Task ResourceRotation_PrefersUnusedResources()
    {
        // Arrange: 3 resources, history shows first two used recently
        var res1 = _fixture.SeedResource(
            id: "rotation-1", title: "Resource A",
            mediaType: "Podcast", transcript: "Text A", vocabWordCount: 5);
        var res2 = _fixture.SeedResource(
            id: "rotation-2", title: "Resource B",
            mediaType: "Podcast", transcript: "Text B", vocabWordCount: 5);
        var res3 = _fixture.SeedResource(
            id: "rotation-3", title: "Resource C",
            mediaType: "Podcast", transcript: "Text C", vocabWordCount: 5);
        _fixture.SeedSkill();

        var today = DateTime.UtcNow.Date;

        // res1 used yesterday, res2 used 2 days ago, res3 never used
        _fixture.SeedCompletion(today.AddDays(-1), "Listening", resourceId: "rotation-1");
        _fixture.SeedCompletion(today.AddDays(-2), "Listening", resourceId: "rotation-2");

        // Act
        var plan = await _builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan.PrimaryResource.Should().NotBeNull();

        // res3 has never been used → highest rotation score
        // res1 was yesterday → disqualified
        plan.PrimaryResource!.Id.Should().NotBe("rotation-1",
            "yesterday's resource should be disqualified from selection");
    }

    [Fact]
    public async Task ConsecutivePlans_SelectDifferentResources()
    {
        // Arrange: 3 resources, generate 2 plans with completion records between
        var res1 = _fixture.SeedResource(
            id: "consec-1", title: "Resource Alpha",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 5);
        var res2 = _fixture.SeedResource(
            id: "consec-2", title: "Resource Beta",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 5);
        var res3 = _fixture.SeedResource(
            id: "consec-3", title: "Resource Gamma",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act: Plan 1
        var plan1 = await _builder.BuildPlanAsync();
        plan1.Should().NotBeNull();
        plan1.PrimaryResource.Should().NotBeNull();
        var firstResourceId = plan1.PrimaryResource!.Id;

        // Simulate completion for Plan 1's resource (used today)
        _fixture.SeedCompletion(DateTime.UtcNow.Date, "Listening", resourceId: firstResourceId);

        // Act: Plan 2 (different builder instance to clear state)
        var builder2 = _fixture.CreateBuilder();
        var plan2 = await builder2.BuildPlanAsync();

        // Assert: Plan 2 should use a different resource
        plan2.Should().NotBeNull();
        plan2.PrimaryResource.Should().NotBeNull();
        plan2.PrimaryResource!.Id.Should().NotBe(firstResourceId,
            "consecutive plans should rotate to different resources");
    }

    #endregion

    #region Plan Content Alignment

    [Fact]
    public async Task PlanActivities_AlignWithPrimaryResource()
    {
        // Arrange
        var resource = _fixture.SeedResource(
            id: "aligned-res", title: "Aligned Resource",
            mediaType: "Podcast", transcript: "Transcript text",
            vocabWordCount: 8);
        _fixture.SeedSkill();

        // Due vocab from this resource
        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var plan = await _builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan.PrimaryResource.Should().NotBeNull();

        // All resource-based activities should reference the primary resource
        var resourceActivities = plan.Activities
            .Where(a => !string.IsNullOrEmpty(a.ResourceId))
            .ToList();

        if (resourceActivities.Any())
        {
            resourceActivities.Should().AllSatisfy(a =>
                a.ResourceId.Should().Be(plan.PrimaryResource!.Id,
                    "all resource-based activities should use the primary resource"));
        }
    }

    [Fact]
    public async Task PlanTotalMinutes_MatchesSessionPreference()
    {
        // Arrange: user prefers 20-minute sessions
        var resource = _fixture.SeedResource(
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act
        var plan = await _builder.BuildPlanAsync();

        // Assert: total time should be reasonable relative to preference
        plan.Should().NotBeNull();
        plan.TotalMinutes.Should().BeGreaterThan(0);
        plan.TotalMinutes.Should().BeLessOrEqualTo(30,
            "plan should not dramatically exceed the 20-minute preference");
    }

    #endregion
}
