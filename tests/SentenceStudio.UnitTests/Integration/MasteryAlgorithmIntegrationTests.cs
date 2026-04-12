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
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<VocabularyProgressService>());
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
        // EffectiveStreak = 1 + 0*0.5 = 1.0 → MasteryScore = 1.0/7.0 ≈ 0.143
        result.MasteryScore.Should().BeApproximately(1.0f / 7.0f, 0.01f);
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
        // EffectiveStreak = 1 + 1*0.5 = 1.5 → MasteryScore = 1.5/7.0 ≈ 0.214
        result.MasteryScore.Should().BeApproximately(1.5f / 7.0f, 0.01f);
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
        masteryBefore.Should().BeGreaterThan(0, "should have built up mastery");

        // Act: wrong answer
        var result = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false, inputMode: "MultipleChoice"));

        // Assert
        result.CurrentStreak.Should().Be(0, "streak resets on wrong answer");
        result.ProductionInStreak.Should().Be(0, "production streak resets on wrong answer");
        result.MasteryScore.Should().BeApproximately(masteryBefore * 0.6f, 0.01f,
            "mastery should be penalized by 60% factor");
    }

    [Fact]
    public async Task RecordAttempt_StreakOf7Recognition_ReachesMasteryThresholdButNotKnown()
    {
        // Arrange: 7 consecutive correct MC answers
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        // Act: 7 correct recognition attempts
        VocabularyProgress? result = null;
        for (int i = 0; i < 7; i++)
        {
            result = await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }

        // Assert
        result!.CurrentStreak.Should().Be(7);
        result.ProductionInStreak.Should().Be(0);
        // EffectiveStreak = 7 + 0 = 7 → MasteryScore = 7/7 = 1.0
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

        // Phase 2: Build up recognition streak (MC only)
        for (int i = 0; i < 4; i++)
        {
            progress = await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        }
        // After 5 MC: EffStreak = 5/7 ≈ 0.71 → should be above promotion threshold (0.50)
        progress.MasteryScore.Should().BeGreaterOrEqualTo(0.50f,
            "after 5 correct MC answers, should be above promotion threshold");
        progress.IsKnown.Should().BeFalse("still no production attempts");

        // Phase 3: Switch to production mode (Text input)
        progress = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));
        progress.ProductionInStreak.Should().Be(1);
        progress.IsKnown.Should().BeFalse("need 2 production attempts");

        progress = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));
        progress.ProductionInStreak.Should().Be(2);

        // EffStreak = 7 + 2*0.5 = 8 → Mastery = min(8/7, 1.0) = 1.0
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

        // 5 MC + 2 Text = Known
        for (int i = 0; i < 5; i++)
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

        // 4 MC + 2 Text
        for (int i = 0; i < 4; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "MultipleChoice"));
        for (int i = 0; i < 2; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: true, inputMode: "Text"));

        var beforeWrong = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        beforeWrong.MasteryScore.Should().BeGreaterOrEqualTo(0.85f);

        // Act: wrong answer
        var result = await _progressService.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false, inputMode: "Text"));

        // Assert: mastery drops, streaks reset → no longer Known
        result.CurrentStreak.Should().Be(0);
        result.ProductionInStreak.Should().Be(0);
        result.MasteryScore.Should().BeLessThan(0.85f, "40% penalty drops below threshold");
        result.IsKnown.Should().BeFalse();
        result.Status.Should().Be(LearningStatus.Learning);
    }

    #endregion

    #region IsPromoted (Production Mode) Threshold

    [Fact]
    public async Task IsPromoted_SetAt50PercentMastery()
    {
        // Arrange: need EffectiveStreak >= 3.5 for MasteryScore >= 0.50
        // 4 MC: EffStreak = 4/7 ≈ 0.57 → above 0.50
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        VocabularyProgress? result = null;
        for (int i = 0; i < 4; i++)
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
        // Arrange: 2 MC: EffStreak = 2/7 ≈ 0.286 → below 0.50
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

        // 10 Text attempts: EffStreak = 10 + 10*0.5 = 15 → 15/7 = 2.14, capped at 1.0
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

        // Then 5 wrong answers: mastery *= 0.6 each time
        for (int i = 0; i < 5; i++)
            await _progressService.RecordAttemptAsync(
                MakeAttempt(wordId, wasCorrect: false, inputMode: "MultipleChoice"));

        var result = await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        // After 3 correct MC: EffStreak = 3/7 ≈ 0.4286
        // After 5 wrong: 0.4286 * 0.6^5 ≈ 0.033
        result.MasteryScore.Should().BeLessThan(0.05f, "multiple penalties should drive mastery near zero");
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
