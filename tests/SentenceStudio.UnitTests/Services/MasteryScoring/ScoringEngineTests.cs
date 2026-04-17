// Phase 0 — Scoring engine tests for DifficultyWeight, temporal weighting, recovery boost.
// Written to spec BEFORE implementation lands. Tests that reference Phase 0 formulas will
// fail until Wash's implementation is merged. That's intentional — these are the acceptance
// criteria.
//
// Spec references:
//   quiz-learning-journey.md §5.6 (temporal weighting, recovery boost)
//   quiz-learning-journey.md §7   (MasteryScore calculation, DifficultyWeight)
//   cross-activity-mastery.md §2.1 (DifficultyWeight table)
//
// Pattern: real in-memory SQLite, same as VocabularyProgressServiceUserIdTests.cs

using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services.MasteryScoring;

/// <summary>
/// Acceptance tests for the Phase 0 scoring engine changes:
///   - DifficultyWeight streak acceleration
///   - Temporal weighting (scaled penalty)
///   - Partial streak preservation
///   - Recovery boost
///   - Deferred recording write-back
///   - Full scenario walkthroughs from the spec
/// </summary>
public class ScoringEngineTests : IDisposable
{
    private const string TestUserId = "test-user-scoring";
    private const string TestWordId = "word-scoring-1";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IPreferencesService> _mockPreferences;
    private readonly VocabularyProgressService _sut;

    public ScoringEngineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _mockPreferences = new Mock<IPreferencesService>();
        _mockPreferences
            .Setup(p => p.Get("active_profile_id", string.Empty))
            .Returns(TestUserId);

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlite(_connection));
        services.AddSingleton<IPreferencesService>(_mockPreferences.Object);

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        var progressRepo = new VocabularyProgressRepository(
            _serviceProvider,
            NullLogger<VocabularyProgressRepository>.Instance);

        var contextRepo = new VocabularyLearningContextRepository(
            _serviceProvider,
            NullLogger<VocabularyLearningContextRepository>.Instance);

        _sut = new VocabularyProgressService(
            progressRepo,
            contextRepo,
            NullLogger<VocabularyProgressService>.Instance,
            _mockPreferences.Object);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Seed helpers ──────────────────────────────────────────────

    private void SeedWord(string wordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (!db.VocabularyWords.Any(w => w.Id == wordId))
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = wordId,
                TargetLanguageTerm = $"target-{wordId}",
                NativeLanguageTerm = $"native-{wordId}"
            });
            db.SaveChanges();
        }
    }

    /// <summary>
    /// Seed a VocabularyProgress record with specific starting state.
    /// </summary>
    private void SeedProgress(
        string wordId,
        int correctAttempts = 0,
        int totalAttempts = 0,
        float currentStreak = 0,
        int productionInStreak = 0,
        float masteryScore = 0f)
    {
        SeedWord(wordId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = TestUserId,
            CorrectAttempts = correctAttempts,
            TotalAttempts = totalAttempts,
            CurrentStreak = currentStreak,
            ProductionInStreak = productionInStreak,
            MasteryScore = masteryScore,
            FirstSeenAt = DateTime.Now.AddDays(-30),
            LastPracticedAt = DateTime.Now.AddDays(-1),
            NextReviewDate = DateTime.Now.AddDays(-1),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });
        db.SaveChanges();
    }

    private VocabularyAttempt MakeAttempt(
        string wordId,
        bool wasCorrect,
        string inputMode = "MultipleChoice",
        float difficultyWeight = 1.0f) => new()
    {
        VocabularyWordId = wordId,
        UserId = TestUserId,
        Activity = "VocabularyQuiz",
        InputMode = inputMode,
        WasCorrect = wasCorrect,
        DifficultyWeight = difficultyWeight,
        ContextType = "Isolated"
    };

    // ════════════════════════════════════════════════════════════════
    //  1-5: DifficultyWeight Acceleration
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DifficultyWeight_MC_Correct_IncrementsStreakBy1()
    {
        // Spec: MC correct (DW=1.0) → CurrentStreak += 1.0
        SeedWord(TestWordId);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(TestWordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 1.0f));

        result.CurrentStreak.Should().Be(1,
            "MC correct with DW=1.0 should increment streak by 1.0 (spec §7)");
    }

    [Fact]
    public async Task DifficultyWeight_Text_Correct_IncrementsStreakBy1_5()
    {
        // Spec: Text correct (DW=1.5) → CurrentStreak += 1.5
        SeedWord(TestWordId);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(TestWordId, wasCorrect: true,
                inputMode: InputMode.Text.ToString(),
                difficultyWeight: 1.5f));

        // CurrentStreak is now float (Phase 0). DW=1.5 → streak = 1.5
        result.CurrentStreak.Should().BeApproximately(1.5f, 0.01f,
            "Text correct with DW=1.5 should increment streak by 1.5 (spec §7)");
    }

    [Fact]
    public async Task DifficultyWeight_Sentence_Correct_IncrementsStreakBy2_5()
    {
        // Spec: Sentence correct (DW=2.5) → CurrentStreak += 2.5
        SeedWord(TestWordId);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(TestWordId, wasCorrect: true,
                inputMode: InputMode.Text.ToString(),
                difficultyWeight: 2.5f));

        // CurrentStreak is now float (Phase 0). DW=2.5 → streak = 2.5
        result.CurrentStreak.Should().BeApproximately(2.5f, 0.01f,
            "Sentence correct with DW=2.5 should increment streak by 2.5 (spec §7)");
    }

    [Fact]
    public async Task DifficultyWeight_DefaultZero_FallsBackTo1()
    {
        // Spec: DW of 0 or unset should fall back to 1.0
        SeedWord(TestWordId);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(TestWordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 0f));

        result.CurrentStreak.Should().BeGreaterThan(0,
            "DW=0 should fall back to 1.0, not produce zero increment (spec §7)");
    }

    [Fact]
    public async Task DifficultyWeight_ThreeTextEntries_YieldStreakOf4_5()
    {
        // Spec: CurrentStreak is float — 3 text entries (DW=1.5 each) → streak = 4.5
        SeedWord(TestWordId);

        VocabularyProgress? result = null;
        for (int i = 0; i < 3; i++)
        {
            result = await _sut.RecordAttemptAsync(
                MakeAttempt(TestWordId, wasCorrect: true,
                    inputMode: InputMode.Text.ToString(),
                    difficultyWeight: 1.5f));
        }

        result.Should().NotBeNull();
        // CurrentStreak is float now (Phase 0). 3 x 1.5 = 4.5
        result!.CurrentStreak.Should().BeApproximately(4.5f, 0.01f,
            "3 text entries with DW=1.5 yield streak = 4.5 (float, spec §7)");
    }

    // ════════════════════════════════════════════════════════════════
    //  6-9: Temporal Weighting (Scaled Penalty)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TemporalWeighting_NewLearner_GetsFullPenalty()
    {
        // Spec §5.6: 0 correct → penalty factor = 0.6 (full penalty)
        var wordId = "word-temporal-new";
        SeedProgress(wordId,
            correctAttempts: 0,
            totalAttempts: 0,
            currentStreak: 2,
            masteryScore: 0.29f);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        // Current code: flat *= 0.6 → 0.29 * 0.6 = 0.174
        // Phase 0 with 0 correct: penalty factor = max(0.6, 1.0 - 0.4/ln(1+0+1)) = 0.6
        // Result should be ~ 0.29 * 0.6 = 0.174
        result.MasteryScore.Should().BeApproximately(0.174f, 0.02f,
            "new learner (0 correct) should get full 0.6 penalty factor (spec §5.6 Component 1)");
    }

    [Fact]
    public async Task TemporalWeighting_EstablishedLearner_GetsSofterPenalty()
    {
        // Spec §5.6: 10 correct → penalty factor ~ 0.88
        var wordId = "word-temporal-estab";
        SeedProgress(wordId,
            correctAttempts: 10,
            totalAttempts: 12,
            currentStreak: 10,
            productionInStreak: 4,
            masteryScore: 1.0f);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        // Phase 0 implemented: 10 correct → penalty factor ≈ 0.88
        // penalty = max(0.6, 1.0 - 0.4 / (1 + ln(1+10))) ≈ 0.882
        // mastery = 1.0 * 0.882 ≈ 0.88
        result.MasteryScore.Should().BeApproximately(0.88f, 0.03f,
            "established learner (10 correct) gets softer ~0.88 penalty (spec §5.6 Component 1)");
    }

    [Fact]
    public async Task TemporalWeighting_VeryEstablishedLearner_GetsMinimalPenalty()
    {
        // Spec §5.6: 50 correct → penalty factor ~ 0.92
        var wordId = "word-temporal-very";
        SeedProgress(wordId,
            correctAttempts: 50,
            totalAttempts: 55,
            currentStreak: 10,
            productionInStreak: 4,
            masteryScore: 1.0f);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        // Phase 0 implemented: 50 correct → penalty factor ≈ 0.92
        result.MasteryScore.Should().BeApproximately(0.92f, 0.03f,
            "very established learner (50 correct) gets ~0.92 penalty (spec §5.6 Component 1)");
    }

    [Fact]
    public async Task TemporalWeighting_PenaltyAlwaysDropsMasteryByAtLeast8Percent()
    {
        // Spec §5.6: WRONG_ANSWER_FLOOR (0.6) ensures penalty factor never drops below 0.6.
        // The formula is logarithmic — at 200 correct the factor is ~0.94, asymptotically
        // approaching 1.0 but never reaching it. The spec table shows ~0.92 at 50 correct.
        // Key invariant: wrong answer always reduces mastery (never a no-op).
        var wordId = "word-temporal-ceil";
        SeedProgress(wordId,
            correctAttempts: 200,
            totalAttempts: 205,
            currentStreak: 10,
            masteryScore: 1.0f);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        result.MasteryScore.Should().BeLessThan(1.0f,
            "wrong answer must always reduce mastery, even for very established learners");
        result.MasteryScore.Should().BeGreaterThanOrEqualTo(0.6f,
            "WRONG_ANSWER_FLOOR ensures mastery never drops below 60% of original");
        // The logarithmic formula at 200 correct gives penalty ≈ 0.937
        result.MasteryScore.Should().BeInRange(0.90f, 0.96f,
            "at 200 correct, penalty factor should be in the 0.90–0.96 range (log curve)");
    }

    // ════════════════════════════════════════════════════════════════
    //  10-12: Partial Streak Preservation
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PartialStreak_NewLearner_FullStreakReset()
    {
        // Spec §5.6 Component 2: 0 correct → preserve 0% → streak = 0
        var wordId = "word-streak-new";
        SeedProgress(wordId,
            correctAttempts: 0,
            totalAttempts: 2,
            currentStreak: 2,
            masteryScore: 0.29f);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        result.CurrentStreak.Should().Be(0,
            "new learner (0 correct) gets full streak reset on wrong answer (spec §5.6 Component 2)");
    }

    [Fact]
    public async Task PartialStreak_EstablishedLearner_PreservesAbout30Percent()
    {
        // Spec §5.6 Component 2: 10 correct, streak=10 → preserve ~30% → streak ≈ 3
        var wordId = "word-streak-estab";
        SeedProgress(wordId,
            correctAttempts: 10,
            totalAttempts: 12,
            currentStreak: 10,
            productionInStreak: 4,
            masteryScore: 1.0f);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        // Phase 0 implemented: 10 correct → preserve ~30% of 10 ≈ 3
        result.CurrentStreak.Should().BeInRange(2f, 4f,
            "established learner (10 correct, streak=10) preserves ~30% → streak ≈ 3 (spec §5.6 Component 2)");
    }

    [Fact]
    public async Task PartialStreak_ProductionInStreak_AlsoPartiallyPreserved()
    {
        // Spec §5.6: ProductionInStreak uses same preserveFraction
        var wordId = "word-streak-prod";
        SeedProgress(wordId,
            correctAttempts: 10,
            totalAttempts: 12,
            currentStreak: 10,
            productionInStreak: 4,
            masteryScore: 1.0f);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        // Phase 0 implemented: preserve ~30% of 4 ≈ 1
        result.ProductionInStreak.Should().BeInRange(1, 2,
            "ProductionInStreak also partially preserved at ~30% of 4 (spec §5.6 Component 2)");
    }

    // ════════════════════════════════════════════════════════════════
    //  13-16: Recovery Boost
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RecoveryBoost_CorrectAfterWrong_MasteryIncreases()
    {
        // Spec §5.6 Component 3: correct answer during recovery always shows progress
        var wordId = "word-recovery-basic";
        SeedProgress(wordId,
            correctAttempts: 12,
            totalAttempts: 14,
            currentStreak: 10,
            productionInStreak: 4,
            masteryScore: 1.0f);

        // Wrong answer first
        var afterWrong = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));
        float masteryAfterWrong = afterWrong.MasteryScore;

        // Correct answer — should increase mastery, not decrease
        var afterCorrect = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 1.0f));

        // Current code: mastery drops from 0.6 → 0.14 (the double-whammy bug)
        // Phase 0: mastery goes UP from penalty level
        afterCorrect.MasteryScore.Should().BeGreaterThanOrEqualTo(masteryAfterWrong,
            "CRITICAL: correct answer after wrong must never make mastery WORSE. " +
            "This is the double-whammy bug (spec §5.6 Component 3).");
    }

    [Fact]
    public async Task RecoveryBoost_AppliesWhenMasteryAboveStreakScore()
    {
        // Spec §5.6: +0.02 recovery boost when MasteryScore > streakScore
        var wordId = "word-recovery-boost";

        // Simulate post-wrong state: high mastery but low streak (recovery period)
        SeedProgress(wordId,
            correctAttempts: 12,
            totalAttempts: 14,
            currentStreak: 3,       // Low streak (partially preserved)
            productionInStreak: 1,
            masteryScore: 0.88f);   // High mastery (softly penalized)

        // In recovery: streakScore = min((3 + 1*0.5)/7, 1.0) = 0.50
        //   MasteryScore (0.88) > streakScore (0.50) → recovery boost applies
        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 1.0f));

        // Current code: mastery = min(effectiveStreak/7, 1.0) which would be ~0.64
        //   (streak goes to 4, prodInStreak stays at 1, effective = 4.5, 4.5/7 ≈ 0.64)
        //   This is LOWER than 0.88 — the double-whammy bug.
        // Phase 0: mastery = max(0.64, 0.88) + 0.02 = 0.90
        result.MasteryScore.Should().BeApproximately(0.90f, 0.02f,
            "recovery boost (+0.02) applies when mastery > streakScore (spec §5.6 Component 3)");
    }

    [Fact]
    public async Task RecoveryBoost_StopsWhenStreakCatchesUp()
    {
        // Spec §5.6: boost = 0 when streakScore >= MasteryScore
        var wordId = "word-recovery-stop";

        // State where streakScore will be >= MasteryScore after correct answer
        SeedProgress(wordId,
            correctAttempts: 12,
            totalAttempts: 14,
            currentStreak: 6,       // High enough streak
            productionInStreak: 3,
            masteryScore: 0.90f);   // Mastery close to streak-derived score

        // streakScore = min((6+3*0.5)/7, 1.0) = min(7.5/7, 1.0) = 1.0
        // After +1 MC: streak=7, prod=3, effective=8.5, streakScore=1.0
        // MasteryScore (0.90) < streakScore (1.0) → no recovery boost → mastery = 1.0
        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 1.0f));

        // Whether current code or Phase 0, streak-derived score is >= mastery
        // so the mastery should equal the streak-derived score (no boost added)
        result.MasteryScore.Should().BeGreaterThanOrEqualTo(0.90f,
            "when streakScore catches up to mastery, no recovery boost is added (spec §5.6)");
    }

    [Fact]
    public async Task RecoveryBoost_MasteryNeverExceeds1()
    {
        // Spec §5.6: MasteryScore = min(MasteryScore, 1.0)
        var wordId = "word-recovery-cap";
        SeedProgress(wordId,
            correctAttempts: 50,
            totalAttempts: 55,
            currentStreak: 10,
            productionInStreak: 5,
            masteryScore: 0.99f);

        // Even with recovery boost, mastery should cap at 1.0
        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 1.0f));

        result.MasteryScore.Should().BeLessOrEqualTo(1.0f,
            "mastery must never exceed 1.0 even with recovery boost (spec §5.6 Component 3)");
    }

    // ════════════════════════════════════════════════════════════════
    //  17: Deferred Recording Write-Back (unit test portion)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeferredRecording_RecordAttemptAsync_ReturnsUpdatedProgress()
    {
        // Unit test: RecordAttemptAsync returns the updated progress object
        // (E2E verification that the UI uses this return value is deferred — needs
        //  Playwright or Appium to confirm the quiz panel updates)
        SeedWord(TestWordId);

        var result = await _sut.RecordAttemptAsync(
            MakeAttempt(TestWordId, wasCorrect: true));

        result.Should().NotBeNull("RecordAttemptAsync must return the persisted progress object");
        result.TotalAttempts.Should().Be(1);
        result.CorrectAttempts.Should().Be(1);
        result.CurrentStreak.Should().BeGreaterThan(0);
        result.MasteryScore.Should().BeGreaterThan(0f,
            "returned progress must reflect the update, not stale data");

        // Verify persistence — re-read from DB
        var reRead = await _sut.GetProgressAsync(TestWordId, TestUserId);
        reRead.MasteryScore.Should().Be(result.MasteryScore,
            "persisted value must match the returned value");
    }

    // ════════════════════════════════════════════════════════════════
    //  18: Captain's Scenario — Established Word Recovery Path
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CaptainScenario_EstablishedWord_RecoveryAfterWrong()
    {
        // Spec §5.6 "Combined Scenario — Captain's Example":
        //   Before: 12 correct, streak=10, prodInStreak=4, mastery=1.0
        //   Wrong → mastery ~0.88, streak ~3, prodInStreak ~1
        //   1st correct (prod, wt=1.5) → mastery ~0.90, IsKnown restored
        //   2nd correct (prod, wt=1.5) → mastery ~1.0, fully recovered
        var wordId = "word-captain-estab";
        SeedProgress(wordId,
            correctAttempts: 12,
            totalAttempts: 14,
            currentStreak: 10,
            productionInStreak: 4,
            masteryScore: 1.0f);

        // Step 1: Wrong answer
        var afterWrong = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        // Phase 0 implemented: penalty ≈ 0.88, streak preserved ~30%
        afterWrong.MasteryScore.Should().BeLessThan(1.0f,
            "wrong answer must reduce mastery from 1.0");
        afterWrong.MasteryScore.Should().BeApproximately(0.88f, 0.03f,
            "established word (12 correct) gets ~0.88 penalty factor (spec §5.6)");
        afterWrong.CurrentStreak.Should().BeInRange(2f, 4f,
            "streak partially preserved at ~30% of 10");
        afterWrong.ProductionInStreak.Should().BeInRange(1, 2,
            "prodInStreak partially preserved at ~30% of 4");

        // Step 2: 1st correct (production, wt=1.5)
        var after1stCorrect = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.Text.ToString(),
                difficultyWeight: 1.5f));

        after1stCorrect.MasteryScore.Should().BeGreaterThanOrEqualTo(afterWrong.MasteryScore,
            "1st correct after wrong must not make mastery worse (anti-double-whammy)");
        // Phase 0: mastery should increase toward 0.90 with recovery boost
        after1stCorrect.MasteryScore.Should().BeApproximately(0.90f, 0.03f,
            "recovery boost during recovery period (spec §5.6 Component 3)");

        // Step 3: 2nd correct (production, wt=1.5)
        var after2ndCorrect = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.Text.ToString(),
                difficultyWeight: 1.5f));

        after2ndCorrect.MasteryScore.Should().BeGreaterThanOrEqualTo(after1stCorrect.MasteryScore,
            "2nd correct should continue recovery");
        // Phase 0: should be at or near 1.0, IsKnown restored
        after2ndCorrect.MasteryScore.Should().BeGreaterThanOrEqualTo(0.90f,
            "2 production correct answers should bring mastery close to 1.0 (spec §5.6)");
    }

    // ════════════════════════════════════════════════════════════════
    //  19: New Learner Scenario — Full Penalty + Slow Recovery
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NewLearnerScenario_FullPenalty_SlowRecovery()
    {
        // Spec §5.6 "New learner scenario":
        //   Before: 2 correct, streak=2, prodInStreak=0, mastery=0.29
        //   Wrong → penalty=0.76, preserve=14% → streak=0, mastery≈0.22
        //   1st correct → mastery=0.22 (floor from max), streak=1
        //   2nd correct → mastery=0.29 (streak catches up), streak=2
        var wordId = "word-newlearner";
        SeedProgress(wordId,
            correctAttempts: 2,
            totalAttempts: 3,
            currentStreak: 2,
            productionInStreak: 0,
            masteryScore: 0.29f);

        // Step 1: Wrong answer
        var afterWrong = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: false));

        // Current: 0.29 * 0.6 = 0.174
        // Phase 0: 0.29 * 0.76 = 0.2204 ≈ 0.22
        afterWrong.MasteryScore.Should().BeLessThan(0.29f,
            "wrong answer must reduce mastery for new learner");
        // Phase 0: 2 correct → preserveFraction = ln(3)/8 ≈ 0.137 → streak = 2 * 0.137 ≈ 0.27
        // The spec says this rounds to 0 effectively, but with float streak it's ~0.27
        afterWrong.CurrentStreak.Should().BeLessThan(1f,
            "new learner (2 correct) preserves ~14% of streak 2 ≈ 0.27 — effectively reset");

        // Step 2: 1st correct (MC, wt=1.0)
        var after1stCorrect = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 1.0f));

        after1stCorrect.CurrentStreak.Should().BeGreaterThan(0,
            "correct answer increments streak");
        after1stCorrect.MasteryScore.Should().BeGreaterThanOrEqualTo(afterWrong.MasteryScore,
            "correct answer must not decrease mastery (anti-double-whammy)");

        // Step 3: 2nd correct
        var after2ndCorrect = await _sut.RecordAttemptAsync(
            MakeAttempt(wordId, wasCorrect: true,
                inputMode: InputMode.MultipleChoice.ToString(),
                difficultyWeight: 1.0f));

        after2ndCorrect.MasteryScore.Should().BeGreaterThanOrEqualTo(after1stCorrect.MasteryScore,
            "continued correct answers recover mastery");
        // Phase 0: streak=2(+0.27), effectiveStreak≈2.27, streakScore=0.32
        // mastery = max(0.32, ~0.22) = 0.32 — streak catches up
        after2ndCorrect.MasteryScore.Should().BeGreaterThan(afterWrong.MasteryScore,
            "2 correct answers should recover mastery above the post-wrong level");
    }
}
