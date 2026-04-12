using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FluentAssertions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.PlanGeneration;

namespace SentenceStudio.UnitTests.Integration;

/// <summary>
/// Integration tests for the Spaced Repetition System (SRS).
/// Tests SM-2 algorithm: ReviewInterval progression, EaseFactor adjustments,
/// NextReviewDate calculation, and interaction with IsKnown filtering.
/// Uses real database via PlanGenerationTestFixture.
/// </summary>
public class SpacedRepetitionIntegrationTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;
    private readonly VocabularyProgressService _progressService;
    private readonly VocabularyProgressRepository _progressRepo;

    public SpacedRepetitionIntegrationTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile();

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

    #region ReviewInterval Progression

    [Fact]
    public async Task FirstCorrectAnswer_SetsReviewIntervalTo6Days()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Act
        var result = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true));

        // Assert: SM-2: first correct → interval = 6
        result.ReviewInterval.Should().Be(6);
        result.NextReviewDate.Should().BeCloseTo(DateTime.Now.AddDays(6), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task SecondCorrectAnswer_MultipliesIntervalByEaseFactor()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // First correct: interval = 6
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));

        // Act: second correct — interval = 6 * EaseFactor, EaseFactor increases
        var result = await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));

        // Assert
        // Default EaseFactor = 2.5, after first correct stays at 2.5 (no change on first)
        // After second correct: interval = 6 * 2.5 = 15, EF = min(2.5, 2.5 + 0.1) = 2.5
        result.ReviewInterval.Should().Be(15);
        result.EaseFactor.Should().Be(2.5f, "EaseFactor is capped at 2.5");
    }

    [Fact]
    public async Task IncorrectAnswer_ResetsIntervalTo1Day()
    {
        // Arrange: build up an interval
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));

        var before = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        before.ReviewInterval.Should().BeGreaterThan(1, "should have grown interval");

        // Act: wrong answer
        var result = await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: false));

        // Assert
        result.ReviewInterval.Should().Be(1, "incorrect answer resets interval to 1");
        result.NextReviewDate.Should().BeCloseTo(DateTime.Now.AddDays(1), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task IncorrectAnswer_ReducesEaseFactorBy02()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));
        var before = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        var easeBefore = before.EaseFactor;

        // Act
        var result = await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: false));

        // Assert
        result.EaseFactor.Should().BeApproximately(easeBefore - 0.2f, 0.01f);
    }

    [Fact]
    public async Task EaseFactor_NeverGoesBelow1Point3()
    {
        // Arrange: many wrong answers to drive EF down
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Start with one correct to create the record
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));

        // 10 wrong answers: EF starts at 2.5, decreases by 0.2 each time
        // 2.5 → 2.3 → 2.1 → 1.9 → 1.7 → 1.5 → 1.3 → clamped at 1.3
        for (int i = 0; i < 10; i++)
            await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: false));

        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        result.EaseFactor.Should().Be(1.3f, "EaseFactor is clamped at minimum 1.3");
    }

    [Fact]
    public async Task ReviewInterval_CappedAt365Days()
    {
        // Arrange: many correct answers to grow interval
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Simulate many correct answers — but this word won't reach Known status
        // because we're only doing MC, so ProductionInStreak stays 0.
        // We need to avoid triggering the Known check (which overrides to 60 days).
        // Use MC-only so word stays in Learning while building a long interval.
        for (int i = 0; i < 20; i++)
        {
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }

        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        result.ReviewInterval.Should().BeLessOrEqualTo(365,
            "review interval should be capped at 365 days maximum");
    }

    #endregion

    #region SRS Interval Recovery After Failure

    [Fact]
    public async Task AfterReset_IntervalProgression_RestartsFromScratch()
    {
        // Arrange: build up interval, then fail, then recover
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Build up
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true)); // interval = 6
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true)); // interval = 15

        // Reset
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: false)); // interval = 1

        // Act: recover
        var result = await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));

        // Assert: first correct after reset → interval = 6 (restart)
        result.ReviewInterval.Should().Be(6);
    }

    #endregion

    #region GetDueVocabularyAsync / GetDueVocabCountAsync

    [Fact]
    public async Task GetDueVocabCountAsync_CountsOnlyDueNonKnownWords()
    {
        // Arrange: seed mix of due/not-due, known/not-known words
        var resource = _fixture.SeedResource(vocabWordCount: 6);
        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // Words 0,1: due for review (past NextReviewDate), not known
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[0], masteryScore: 0.3f,
            nextReviewDate: DateTime.UtcNow.AddDays(-1), resourceId: resource.Id);
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[1], masteryScore: 0.5f,
            nextReviewDate: DateTime.UtcNow.AddDays(-2), resourceId: resource.Id);

        // Word 2: due but KNOWN (should be excluded)
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[2], masteryScore: 0.90f,
            productionInStreak: 3, currentStreak: 6,
            nextReviewDate: DateTime.UtcNow.AddDays(-1), resourceId: resource.Id);

        // Word 3: not yet due (future NextReviewDate)
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[3], masteryScore: 0.4f,
            nextReviewDate: DateTime.UtcNow.AddDays(5), resourceId: resource.Id);

        // Words 4,5: no progress record

        // Act
        var dueCount = await _progressRepo.GetDueVocabCountAsync(DateTime.UtcNow);

        // Assert: only words 0 and 1 are due and not known
        dueCount.Should().Be(2);
    }

    [Fact]
    public async Task GetDueVocabularyAsync_ExcludesKnownWords()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 4);
        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // Word 0: due, learning
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[0], masteryScore: 0.4f,
            nextReviewDate: DateTime.UtcNow.AddDays(-1), resourceId: resource.Id);

        // Word 1: due, known (mastery >= 0.85 AND production >= 2)
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[1], masteryScore: 0.90f,
            productionInStreak: 3, currentStreak: 7,
            nextReviewDate: DateTime.UtcNow.AddDays(-1), resourceId: resource.Id);

        // Word 2: due, high mastery but no production (not known)
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[2], masteryScore: 0.95f,
            productionInStreak: 0, currentStreak: 8,
            nextReviewDate: DateTime.UtcNow.AddDays(-1), resourceId: resource.Id);

        // Word 3: due, low mastery with production (not known)
        _fixture.SeedVocabularyProgress(
            vocabularyWordId: wordIds[3], masteryScore: 0.60f,
            productionInStreak: 3, currentStreak: 4,
            nextReviewDate: DateTime.UtcNow.AddDays(-1), resourceId: resource.Id);

        // Act
        var dueWords = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow);

        // Assert: word 1 excluded (known), words 0, 2, 3 included
        dueWords.Should().HaveCount(3);
        dueWords.Select(w => w.VocabularyWordId).Should().NotContain(wordIds[1],
            "known words (mastery >= 0.85 AND production >= 2) must be excluded");
        dueWords.Select(w => w.VocabularyWordId).Should().Contain(wordIds[2],
            "high mastery without production is NOT known and should be included");
    }

    [Fact]
    public async Task GetDueVocabularyAsync_ReturnsEmptyWhenNothingDue()
    {
        // Arrange: all words have future review dates
        var resource = _fixture.SeedResource(vocabWordCount: 3);
        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId, masteryScore: 0.4f,
                nextReviewDate: DateTime.UtcNow.AddDays(7), resourceId: resource.Id);
        }

        // Act
        var dueWords = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow);

        // Assert
        dueWords.Should().BeEmpty("no words are due when all have future review dates");
    }

    #endregion

    #region SRS Interacts With Mastery Lifecycle

    [Fact]
    public async Task KnownWord_HasReviewIn60Days_AfterAllAttempts()
    {
        // Arrange: drive word to Known through real attempts
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // 5 MC + 2 Text → Known
        for (int i = 0; i < 5; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        for (int i = 0; i < 2; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));

        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);

        // Assert: Known words get 60-day override
        result.IsKnown.Should().BeTrue();
        result.ReviewInterval.Should().Be(60);

        // Verify it won't show up in due words
        var dueWords = await _progressRepo.GetDueVocabularyAsync(DateTime.UtcNow);
        dueWords.Select(w => w.VocabularyWordId).Should().NotContain(wordId,
            "known word with 60-day interval should not be due today");
    }

    [Fact]
    public async Task SRS_AfterWrongAnswer_ReviewIsTomorrow()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Build up, then fail
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: true));
        await _progressService.RecordAttemptAsync(MakeAttempt(wordId, wasCorrect: false));

        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);

        // Assert: after wrong answer, review is tomorrow (interval = 1)
        result.ReviewInterval.Should().Be(1);
        result.NextReviewDate.Should().BeCloseTo(DateTime.Now.AddDays(1), TimeSpan.FromMinutes(5));
    }

    #endregion

    #region Learning Context Recording

    [Fact]
    public async Task RecordAttempt_SavesLearningContext()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Act
        await _progressService.RecordAttemptAsync(new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            Activity = "VocabularyQuiz",
            InputMode = "MultipleChoice",
            WasCorrect = true,
            LearningResourceId = resource.Id,
            ContextType = "Isolated",
            ResponseTimeMs = 1234,
            UserInput = "답",
            ExpectedAnswer = "답"
        });

        // Assert: verify the learning context was persisted
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var contexts = await db.VocabularyLearningContexts.ToListAsync();
        contexts.Should().HaveCount(1);
        contexts[0].Activity.Should().Be("VocabularyQuiz");
        contexts[0].InputMode.Should().Be("MultipleChoice");
        contexts[0].WasCorrect.Should().BeTrue();
        contexts[0].LearningResourceId.Should().Be(resource.Id);
        contexts[0].ResponseTimeMs.Should().Be(1234);
    }

    #endregion

    private VocabularyAttempt MakeAttempt(
        string wordId,
        bool wasCorrect,
        string inputMode = "MultipleChoice",
        string? resourceId = null)
    {
        return new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            Activity = "VocabularyQuiz",
            InputMode = inputMode,
            WasCorrect = wasCorrect,
            LearningResourceId = resourceId,
            ContextType = "Isolated",
            ResponseTimeMs = 1500
        };
    }
}
