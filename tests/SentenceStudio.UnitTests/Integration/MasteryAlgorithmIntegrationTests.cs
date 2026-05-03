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
/// Integration tests for the mastery algorithm lifecycle:
/// Unknown → Learning → Known via streak-based scoring with real database.
/// Tests RecordAttemptAsync through VocabularyProgressService with real EF Core + SQLite.
/// </summary>
public class MasteryAlgorithmIntegrationTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;
    private readonly VocabularyProgressService _progressService;
    private readonly VocabularyProgressRepository _progressRepo;

    public MasteryAlgorithmIntegrationTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile();

        var scope = fixture.ServiceProvider.CreateScope();
        _progressRepo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();

        // Build VocabularyProgressService with real repos and real DB
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

    #region Streak-Based Mastery Score Calculation

    [Fact]
    public async Task RecordAttempt_SingleCorrectMultipleChoice_IncreasesStreakAndMastery()
    {
        // Arrange: brand new word
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();
        var attempt = MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice");

        // Act
        var result = await _progressService.RecordAttemptAsync(attempt);

        // Assert
        result.CurrentStreak.Should().Be(1);
        result.ProductionInStreak.Should().Be(0, "MultipleChoice is not production");
        result.TotalAttempts.Should().Be(1);
        result.CorrectAttempts.Should().Be(1);
        // EffectiveStreak = 1 + 0*0.5 = 1.0 → MasteryScore = 1.0/12.0 ≈ 0.083 (#191)
        result.MasteryScore.Should().BeApproximately(1.0f / 12.0f, 0.01f);
        result.IsKnown.Should().BeFalse("one correct answer is not enough");
        result.Status.Should().Be(LearningStatus.Learning);
    }

    [Fact]
    public async Task RecordAttempt_SingleCorrectText_IncreasesProductionInStreak()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();
        var attempt = MakeAttempt(wordId, wasCorrect: true, inputMode: "Text");

        // Act
        var result = await _progressService.RecordAttemptAsync(attempt);

        // Assert
        result.CurrentStreak.Should().Be(1);
        result.ProductionInStreak.Should().Be(1, "Text input is production");
        // EffectiveStreak = 1 + 1*0.5 = 1.5 → MasteryScore = 1.5/12.0 ≈ 0.125 (#191)
        result.MasteryScore.Should().BeApproximately(1.5f / 12.0f, 0.01f);
    }

    [Fact]
    public async Task RecordAttempt_WrongAnswer_ResetsStreaksAndPenalizesMastery()
    {
        // Arrange: word with existing progress
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Build up some mastery first
        for (int i = 0; i < 3; i++)
        {
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }

        var beforeWrong = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        var masteryBefore = beforeWrong.MasteryScore;
        var streakBefore = beforeWrong.CurrentStreak;
        masteryBefore.Should().BeGreaterThan(0, "should have built up mastery");

        // Act: wrong answer
        var result = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false, inputMode: "MultipleChoice"));

        // Assert: Phase 0 — partial streak preservation and scaled penalty
        result.CurrentStreak.Should().BeGreaterThan(0, "partial streak preservation keeps some streak");
        result.CurrentStreak.Should().BeLessThan(streakBefore, "streak is reduced on wrong answer");
        result.ProductionInStreak.Should().Be(0, "no production attempts were made");
        result.MasteryScore.Should().BeLessThan(masteryBefore, "mastery penalized on wrong answer");
        result.MasteryScore.Should().BeGreaterThan(masteryBefore * 0.6f,
            "scaled penalty is softer than flat 0.6 for words with history");
    }

    [Fact]
    public async Task RecordAttempt_StreakOf12Recognition_ReachesMasteryCapButNotKnown()
    {
        // Arrange: 12 consecutive correct MC answers — saturates the new
        // EFFECTIVE_STREAK_DIVISOR=12.0 ceiling (#191).
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Act: 12 correct recognition attempts
        VocabularyProgress? result = null;
        for (int i = 0; i < 12; i++)
        {
            result = await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }

        // Assert
        result!.CurrentStreak.Should().BeApproximately(12.0f, 0.01f);
        result.ProductionInStreak.Should().Be(0);
        // EffectiveStreak = 12 + 0 = 12 → MasteryScore = 12/12 = 1.0
        // (recovery boost may push slightly above 1.0 but is clamped)
        result.MasteryScore.Should().Be(1.0f);
        result.IsKnown.Should().BeFalse(
            "IsKnown requires ProductionInStreak >= 2 even with max mastery score");
    }

    #endregion

    #region Full Lifecycle: Unknown → Learning → Known

    [Fact]
    public async Task FullLifecycle_Unknown_To_Learning_To_Known()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Phase 1: Unknown → Learning (first correct answer)
        var progress = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        progress.Status.Should().Be(LearningStatus.Learning,
            "after first attempt, word should be Learning");
        progress.IsKnown.Should().BeFalse();

        // Phase 2: Build up recognition streak (MC only) — under #191's
        // EFFECTIVE_STREAK_DIVISOR=12.0, need 7 MC to clear the 0.50 promotion
        // threshold (was 4 under divisor 7.0). We do 8 here so the final
        // 8 MC + 2 Text combo lands at mastery >= 0.85 for IsKnown.
        for (int i = 0; i < 7; i++)
        {
            progress = await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }
        // After 8 MC: EffStreak = 8/12 ≈ 0.667 → above promotion threshold (0.50)
        progress.MasteryScore.Should().BeGreaterOrEqualTo(0.50f,
            "after 8 correct MC answers, should be above promotion threshold");
        progress.IsKnown.Should().BeFalse("still no production attempts");

        // Phase 3: Switch to production mode (Text input)
        progress = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));
        progress.ProductionInStreak.Should().Be(1);
        progress.IsKnown.Should().BeFalse("need 2 production attempts");

        progress = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));
        progress.ProductionInStreak.Should().Be(2);

        // After 8 MC + 2 Text: streak = 10, prodInStreak = 2,
        // EffStreak = 10 + 2*0.5 = 11 → Mastery = 11/12 ≈ 0.917
        progress.MasteryScore.Should().BeGreaterOrEqualTo(0.85f);
        progress.IsKnown.Should().BeTrue(
            "MasteryScore >= 0.85 AND ProductionInStreak >= 2 → Known");
        progress.Status.Should().Be(LearningStatus.Known);
        progress.MasteredAt.Should().NotBeNull("should record mastery timestamp");
    }

    [Fact]
    public async Task FullLifecycle_KnownWord_GetsLongReviewInterval()
    {
        // Arrange: drive a word to Known
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // 8 MC + 2 Text = Known (divisor 12.0 — #191: streak=10, prodIn=2, eff=11, mastery≈0.917)
        for (int i = 0; i < 8; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        for (int i = 0; i < 2; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));

        // Act: read back the final state
        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);

        // Assert: Known words always get 60 day review interval
        result.IsKnown.Should().BeTrue();
        result.ReviewInterval.Should().Be(60, "known words get pushed to 60-day review");
        result.NextReviewDate.Should().BeCloseTo(DateTime.Now.AddDays(60), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task WrongAnswer_AfterBuildingMastery_DropsBelowKnown()
    {
        // Arrange: build up to near-known level
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // 8 MC + 2 Text (divisor 12.0 — #191: drives word to Known before wrong answer)
        for (int i = 0; i < 8; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        for (int i = 0; i < 2; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));

        var beforeWrong = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        beforeWrong.MasteryScore.Should().BeGreaterOrEqualTo(0.85f);
        beforeWrong.IsKnown.Should().BeTrue("should be Known before wrong answer");

        // Act: wrong answer
        var result = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false, inputMode: "Text"));

        // Assert: Phase 0 — scaled penalty keeps mastery high but production streak
        // drops below threshold, so IsKnown becomes false
        result.CurrentStreak.Should().BeGreaterThan(0, "partial streak preservation");
        result.ProductionInStreak.Should().BeLessThan(2,
            "production streak drops below min threshold for Known");
        result.IsKnown.Should().BeFalse(
            "no longer Known because ProductionInStreak < 2 after partial preservation");
        result.Status.Should().Be(LearningStatus.Learning);
    }

    #endregion

    #region IsPromoted (Production Mode) Threshold

    [Fact]
    public async Task IsPromoted_SetAt50PercentMastery()
    {
        // Arrange: under #191's EFFECTIVE_STREAK_DIVISOR=12.0, need EffectiveStreak >= 6
        // for MasteryScore >= 0.50. 6 MC: EffStreak = 6/12 = 0.50 → exactly at promotion floor.
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        VocabularyProgress? result = null;
        for (int i = 0; i < 6; i++)
        {
            result = await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }

        // Assert
#pragma warning disable CS0618
        result!.IsPromoted.Should().BeTrue("MasteryScore >= 0.50 → promoted to Text mode");
#pragma warning restore CS0618
    }

    [Fact]
    public async Task NotPromoted_Below50PercentMastery()
    {
        // Arrange: 2 MC under divisor 12.0: EffStreak = 2/12 ≈ 0.167 → below 0.50 (#191)
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        VocabularyProgress? result = null;
        for (int i = 0; i < 2; i++)
        {
            result = await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }

#pragma warning disable CS0618
        result!.IsPromoted.Should().BeFalse("MasteryScore < 0.50 → still in MC mode");
#pragma warning restore CS0618
    }

    #endregion

    #region Voice Input Mode

    [Fact]
    public async Task VoiceInput_CountsAsProduction()
    {
        // Arrange
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Act
        var result = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true, inputMode: "Voice"));

        // Assert
        result.ProductionInStreak.Should().Be(1, "Voice input should count as production");
        result.CurrentStreak.Should().Be(1);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task MasteryScore_CapsAt1Point0()
    {
        // Arrange: drive EffectiveStreak well beyond 7
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // 10 Text attempts: EffStreak = 10*1.5 + 10*0.5 = 20 → 20/12 ≈ 1.67, capped at 1.0 (#191)
        for (int i = 0; i < 10; i++)
        {
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));
        }

        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        result.MasteryScore.Should().Be(1.0f, "mastery is capped at 1.0");
    }

    [Fact]
    public async Task MultiplePenalties_MasteryApproachesZero()
    {
        // Arrange: build some mastery, then wrong answers reduce it
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // 3 correct to build mastery
        for (int i = 0; i < 3; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));

        // Then 5 wrong answers: scaled penalty each time
        for (int i = 0; i < 5; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: false, inputMode: "MultipleChoice"));

        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        // Phase 0: scaled penalty is softer (~0.83x per wrong for 3 correct attempts)
        // Under divisor 12.0 (#191): after 3 correct: mastery ≈ 0.25,
        // then 5 wrongs at ~0.83x each ≈ 0.10
        result.MasteryScore.Should().BeLessThan(0.25f,
            "multiple penalties should drive mastery down significantly");
        result.MasteryScore.Should().BeGreaterOrEqualTo(0f, "mastery should not go negative");
    }

    [Fact]
    public async Task RecordAttempt_NewWord_CreatesProgressRecord()
    {
        // Arrange: seed a word that has NO progress record
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Act: first ever attempt
        var result = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));

        // Assert: a new progress record was created
        result.Should().NotBeNull();
        result.VocabularyWordId.Should().Be(wordId);
        result.UserId.Should().Be(PlanGenerationTestFixture.TestUserId);
        result.TotalAttempts.Should().Be(1);
        result.FirstSeenAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
    }

    #endregion

    private VocabularyAttempt MakeAttempt(
        string wordId,
        bool wasCorrect,
        string inputMode,
        string activity = "VocabularyQuiz",
        string? resourceId = null)
    {
        return new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            Activity = activity,
            InputMode = inputMode,
            WasCorrect = wasCorrect,
            LearningResourceId = resourceId,
            ContextType = "Isolated",
            ResponseTimeMs = 1500
        };
    }
}
