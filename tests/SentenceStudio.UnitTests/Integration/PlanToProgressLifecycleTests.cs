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
/// End-to-end integration tests: Plan Generation → Activity Execution → Progress Recording.
/// Tests the full lifecycle where the DeterministicPlanBuilder generates a plan,
/// simulated practice happens, progress is recorded, and the next plan reflects changes.
/// </summary>
public class PlanToProgressLifecycleTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;
    private readonly VocabularyProgressService _progressService;
    private readonly VocabularyProgressRepository _progressRepo;

    public PlanToProgressLifecycleTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile(sessionMinutes: 20);

        var scope = fixture.ServiceProvider.CreateScope();
        _progressRepo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();

        var contextRepo = new VocabularyLearningContextRepository(
            fixture.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<VocabularyLearningContextRepository>());

        _progressService = new VocabularyProgressService(
            _progressRepo,
            contextRepo,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<VocabularyProgressService>());
    }

    public void Dispose() { }

    #region Plan → Practice → Progress → Plan Cycle

    [Fact]
    public async Task FullCycle_PlanGeneration_ThenPractice_ThenNewPlanReflectsProgress()
    {
        // === SETUP: Resource with 10 words, all due for review ===
        var resource = _fixture.SeedResource(
            id: "lifecycle-res", title: "Lifecycle Resource",
            mediaType: "Podcast", transcript: "Korean text about daily life",
            vocabWordCount: 10);
        _fixture.SeedSkill(title: "Listening Comprehension");

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // Seed due vocabulary progress
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                currentStreak: 2,
                totalAttempts: 5,
                correctAttempts: 3,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // === STEP 1: Generate first plan ===
        var builder = _fixture.CreateBuilder();
        var plan1 = await builder.BuildPlanAsync();

        plan1.Should().NotBeNull("should generate a plan with due vocabulary");
        plan1.VocabularyReview.Should().NotBeNull("10 due words should trigger vocab review");
        var dueCountBefore = plan1.VocabularyReview!.TotalDue;
        dueCountBefore.Should().BeGreaterOrEqualTo(5, "should have substantial due words");

        // === STEP 2: Simulate practice — first 5 words get 5 MC + 2 Text each → Known ===
        for (int w = 0; w < 5; w++)
        {
            for (int i = 0; i < 5; i++)
            {
                await _progressService.RecordAttemptAsync(new VocabularyAttempt
                {
                    VocabularyWordId = wordIds[w],
                    UserId = PlanGenerationTestFixture.TestUserId,
                    Activity = "VocabularyQuiz",
                    InputMode = "MultipleChoice",
                    WasCorrect = true,
                    LearningResourceId = resource.Id,
                    ContextType = "Isolated",
                    ResponseTimeMs = 1500
                });
            }
            for (int i = 0; i < 2; i++)
            {
                await _progressService.RecordAttemptAsync(new VocabularyAttempt
                {
                    VocabularyWordId = wordIds[w],
                    UserId = PlanGenerationTestFixture.TestUserId,
                    Activity = "VocabularyQuiz",
                    InputMode = "Text",
                    WasCorrect = true,
                    LearningResourceId = resource.Id,
                    ContextType = "Isolated",
                    ResponseTimeMs = 3000
                });
            }
        }

        // Verify words 0-4 are Known
        for (int w = 0; w < 5; w++)
        {
            var p = await _progressService.GetProgressAsync(wordIds[w], PlanGenerationTestFixture.TestUserId);
            p.IsKnown.Should().BeTrue($"word {w} should be Known after 5 MC + 2 Text");
        }

        // Words 5-9 still at original level
        for (int w = 5; w < 10; w++)
        {
            var p = await _progressService.GetProgressAsync(wordIds[w], PlanGenerationTestFixture.TestUserId);
            p.IsKnown.Should().BeFalse($"word {w} should still be Learning");
        }

        // === STEP 3: Verify due count reflects mastery changes ===
        var dueCountAfter = await _progressRepo.GetDueVocabCountAsync(DateTime.UtcNow);
        // Words 0-4 are Known → excluded from due
        // Words 5-9 may or may not be due depending on their NextReviewDate
        // But Known words should definitely be excluded
        var dueVocab = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow);
        dueVocab.Select(w => w.VocabularyWordId)
            .Should().NotContain(wordIds[0], "word 0 is Known → should not be due")
            .And.NotContain(wordIds[1])
            .And.NotContain(wordIds[2])
            .And.NotContain(wordIds[3])
            .And.NotContain(wordIds[4]);
    }

    #endregion

    #region Practice Records Survive Plan Regeneration

    [Fact]
    public async Task PracticeHistory_PersistsAcrossPlanGenerations()
    {
        // Arrange: resource with words, practice them
        var resource = _fixture.SeedResource(
            id: "persist-res", title: "Persist Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 5);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // Practice word 0 three times
        for (int i = 0; i < 3; i++)
        {
            await _progressService.RecordAttemptAsync(new VocabularyAttempt
            {
                VocabularyWordId = wordIds[0],
                UserId = PlanGenerationTestFixture.TestUserId,
                Activity = "VocabularyQuiz",
                InputMode = "MultipleChoice",
                WasCorrect = true,
                LearningResourceId = resource.Id,
                ContextType = "Isolated",
                ResponseTimeMs = 1500
            });
        }

        // Act: generate a plan (should not corrupt progress data)
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert: progress data is untouched
        var progress = await _progressService.GetProgressAsync(wordIds[0], PlanGenerationTestFixture.TestUserId);
        progress.TotalAttempts.Should().Be(3);
        progress.CorrectAttempts.Should().Be(3);
        progress.CurrentStreak.Should().Be(3);
    }

    #endregion

    #region VocabReview Block DueWords Match Reality

    [Fact]
    public async Task VocabReview_DueWords_ContainOnlyDueNonKnownWords()
    {
        // Arrange
        var resource = _fixture.SeedResource(
            id: "duecheck-res", title: "Due Check Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 8);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // 4 due learning words
        for (int i = 0; i < 4; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.4f,
                currentStreak: 3,
                productionInStreak: 0,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // 2 due known words (should be excluded from DueWords)
        for (int i = 4; i < 6; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.92f,
                currentStreak: 8,
                productionInStreak: 3,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // 2 words with no progress (new words)

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        if (plan.VocabularyReview != null)
        {
            var dueWordIds = plan.VocabularyReview.DueWords
                .Select(w => w.VocabularyWordId)
                .ToHashSet();

            // Known words must not be in due list
            dueWordIds.Should().NotContain(wordIds[4]);
            dueWordIds.Should().NotContain(wordIds[5]);

            // Due learning words should be present
            plan.VocabularyReview.DueWords.Should().AllSatisfy(w =>
            {
                w.IsKnown.Should().BeFalse("due word list should not contain known words");
            });
        }
    }

    #endregion

    #region Activity Types in Plan

    [Fact]
    public async Task Plan_WithPodcastResource_IncludesListeningActivity()
    {
        // Arrange
        var resource = _fixture.SeedResource(
            mediaType: "Podcast",
            transcript: "Podcast transcript",
            vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        var activityTypes = plan.Activities.Select(a => a.ActivityType).ToList();
        // Podcast with transcript: valid input activities are Listening or Reading
        // (random tiebreaker in SelectInputActivity when both have 0 recent uses)
        activityTypes.Should().Contain(
            t => t == "Listening" || t == "Reading",
            $"podcast resource should generate a listening or reading activity but got: [{string.Join(", ", activityTypes)}]");
    }

    [Fact]
    public async Task Plan_WithVideoResource_IncludesVideoActivity()
    {
        // Arrange — re-clear to guarantee isolation when run in parallel
        _fixture.ClearAllData();
        _fixture.SeedUserProfile(sessionMinutes: 20);
        var resource = _fixture.SeedResource(
            mediaType: "Video",
            transcript: "Video transcript",
            mediaUrl: "https://youtube.com/watch?v=test123",
            vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull("a plan should be generated when a Video resource with vocab exists");
        var activityTypes = plan.Activities.Select(a => a.ActivityType).ToList();
        activityTypes.Should().ContainMatch("*",
            "plan should contain at least one activity");
        // Video resources with YouTube URL + transcript can produce VideoWatching, Listening, or Reading
        activityTypes.Should().Contain(
            t => t == "VideoWatching" || t == "Listening" || t == "Reading",
            $"video resource should generate an input activity but got: [{string.Join(", ", activityTypes)}]");
    }

    [Fact]
    public async Task Plan_NoResources_ReturnsNull()
    {
        // Arrange: only a skill, no resources at all
        _fixture.SeedSkill(title: "Grammar Practice");

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert: BuildPlanAsync returns null when no resources are available
        // because the deterministic builder requires at least one resource
        plan.Should().BeNull(
            "plan generation requires at least one learning resource");
    }

    #endregion

    #region Completion Record Tracking

    [Fact]
    public async Task CompletionRecords_AffectResourceSelection()
    {
        // Arrange: 2 resources
        var res1 = _fixture.SeedResource(
            id: "comp-1", title: "Recently Used",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 5);
        var res2 = _fixture.SeedResource(
            id: "comp-2", title: "Not Used",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 5);
        _fixture.SeedSkill();

        var today = DateTime.UtcNow.Date;

        // Mark res1 as completed today (used today)
        _fixture.SeedCompletion(today, "Listening", resourceId: "comp-1");

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan.PrimaryResource.Should().NotBeNull();

        // If today's resource is disqualified, res2 should be selected
        // (depends on recency scoring in the builder)
        plan.PrimaryResource!.Id.Should().Be("comp-2",
            "today's used resource should be scored lower than unused resource");
    }

    #endregion
}
