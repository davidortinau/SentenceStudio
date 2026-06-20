using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FluentAssertions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.PlanGeneration;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Regression tests for the duplicate-merge learning-history preservation fix.
///
/// Before the fix, <see cref="LearningResourceRepository.MergeVocabularyWordsAsync"/> only reassigned
/// resource mappings and then deleted the duplicate <see cref="VocabularyWord"/> rows — the database's
/// ON DELETE CASCADE then destroyed every duplicate's <see cref="VocabularyProgress"/> (mastery, streak,
/// spaced-repetition schedule) and <see cref="VocabularyLearningContext"/> (per-attempt history). A user
/// who had practiced a word to mastery would see it reset to Unknown after a merge.
///
/// These tests run against the in-memory SQLite fixture, which builds the schema WITH the real cascade
/// relationships and enforces foreign keys, so they genuinely reproduce the original data-loss bug and
/// prove the fix preserves and recalculates history "as if it had always been one word."
/// </summary>
public class VocabularyMergeHistoryTests : IClassFixture<PlanGenerationTestFixture>
{
    private readonly PlanGenerationTestFixture _fixture;

    private const string UserA = PlanGenerationTestFixture.TestUserId;
    private const string UserB = "test-user-2";

    public VocabularyMergeHistoryTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile();
    }

    // A compact description of a learning context to seed.
    private sealed record Ctx(
        DateTime At,
        bool Correct,
        string InputMode = "Text",
        bool Passive = false,
        string Activity = "VocabularyQuiz");

    [Fact]
    public async Task Merge_MovesAndRecalculatesProgress_AsIfAlwaysOneWord()
    {
        // Arrange — a user practiced the same concept under two duplicate word records.
        var t = DateTime.UtcNow.AddDays(-10);
        var keeper = SeedWord("사과", "apple");
        var dup = SeedWord("사과 ", "apple (dup)");

        var keeperProg = SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true, InputMode: "MultipleChoice"),
            new Ctx(t.AddMinutes(3), Correct: true, InputMode: "Text"),
        });
        var dupProg = SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(2), Correct: true, InputMode: "Text"),
            new Ctx(t.AddMinutes(4), Correct: false, InputMode: "Text"),
        });

        var oracle = BuildOracle(keeper, UserA, keeperProg, dupProg);

        // Act
        var deleted = await MergeAsync(keeper, dup);

        // Assert — duplicate word is gone, keeper folded in all four attempts.
        deleted.Should().Be(1);
        WordExists(dup).Should().BeFalse("the duplicate word should be deleted");
        WordExists(keeper).Should().BeTrue("the keeper word must remain");
        GetProgress(dup, UserA).Should().BeNull("the duplicate progress row must not survive");

        var merged = GetProgress(keeper, UserA);
        merged.Should().NotBeNull();
        merged!.TotalAttempts.Should().Be(4, "all four attempts must be preserved");
        merged.CorrectAttempts.Should().Be(3);
        merged.MasteryScore.Should().BeGreaterThan(0f, "mastery must NOT be reset to Unknown");

        // The keeper must equal a full chronological replay of every context (the "one word" oracle).
        merged.MasteryScore.Should().BeApproximately(oracle.MasteryScore, 0.0001f);
        merged.CurrentStreak.Should().BeApproximately(oracle.CurrentStreak, 0.0001f);
        merged.ProductionInStreak.Should().Be(oracle.ProductionInStreak);
        merged.TotalAttempts.Should().Be(oracle.TotalAttempts);
        merged.CorrectAttempts.Should().Be(oracle.CorrectAttempts);
    }

    [Fact]
    public async Task Merge_ReParentsContexts_DoesNotCascadeDelete()
    {
        // Arrange — duplicate carries both active attempts and a passive exposure.
        var t = DateTime.UtcNow.AddDays(-5);
        var keeper = SeedWord("물", "water");
        var dup = SeedWord("물 ", "water (dup)");

        var keeperProg = SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
            new Ctx(t.AddMinutes(2), Correct: true),
        });
        SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(3), Correct: true),
            new Ctx(t.AddMinutes(4), Correct: false),
            new Ctx(t.AddMinutes(5), Correct: false, Passive: true, Activity: "Reading"),
        });

        var keeperProgId = keeperProg.Id;
        var totalContextsBefore = CountAllContexts();
        totalContextsBefore.Should().Be(5);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — every context survives and is re-parented onto the keeper (none cascade-deleted).
        CountAllContexts().Should().Be(5, "no learning context may be destroyed by the merge");
        CountContextsFor(keeperProgId).Should().Be(5, "all contexts must now point at the keeper progress");

        var merged = GetProgress(keeper, UserA)!;
        merged.Id.Should().Be(keeperProgId, "the existing keeper progress row is retained");
        merged.TotalAttempts.Should().Be(4, "the passive exposure is not an attempt");
        merged.ExposureCount.Should().Be(1, "the passive exposure must be preserved as an exposure");
    }

    [Fact]
    public async Task Merge_IsolatesProgressPerUser()
    {
        // Arrange — two users practiced the duplicate; only user A also practiced the keeper.
        SeedUserProfile(UserB);
        var t = DateTime.UtcNow.AddDays(-8);
        var keeper = SeedWord("책", "book");
        var dup = SeedWord("책 ", "book (dup)");

        var keeperProgA = SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
        });
        var dupProgA = SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(2), Correct: true),
            new Ctx(t.AddMinutes(3), Correct: true),
        });
        var dupProgB = SeedProgress(dup, UserB, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
            new Ctx(t.AddMinutes(2), Correct: false),
        });

        var oracleA = BuildOracle(keeper, UserA, keeperProgA, dupProgA);
        var oracleB = BuildOracle(keeper, UserB, dupProgB);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — each user keeps exactly one combined keeper record; users are never collapsed together.
        GetProgress(dup, UserA).Should().BeNull();
        GetProgress(dup, UserB).Should().BeNull();
        CountProgressFor(keeper).Should().Be(2, "exactly one keeper progress per user");

        var mergedA = GetProgress(keeper, UserA)!;
        mergedA.TotalAttempts.Should().Be(3);
        mergedA.MasteryScore.Should().BeApproximately(oracleA.MasteryScore, 0.0001f);

        var mergedB = GetProgress(keeper, UserB)!;
        mergedB.Should().NotBeNull("user B practiced only the duplicate, so a keeper record must be created");
        mergedB.TotalAttempts.Should().Be(2);
        mergedB.MasteryScore.Should().BeApproximately(oracleB.MasteryScore, 0.0001f);
    }

    [Fact]
    public async Task Merge_AncientAggregateHistory_WithoutContexts_NeverRegresses()
    {
        // Arrange — the duplicate holds high mastery from before per-attempt context tracking existed,
        // so there are no contexts to replay. The merge must not zero that mastery out.
        var t = DateTime.UtcNow.AddDays(-30);
        var keeper = SeedWord("학교", "school");
        var dup = SeedWord("학교 ", "school (dup)");

        SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
        });
        SeedAggregateOnlyProgress(dup, UserA,
            masteryScore: 0.9f, totalAttempts: 20, correctAttempts: 18,
            currentStreak: 10f, productionInStreak: 6);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — counts are monotonic and mastery/streak are protected from regression.
        var merged = GetProgress(keeper, UserA)!;
        merged.TotalAttempts.Should().Be(21, "1 context-backed + 20 ancient attempts");
        merged.CorrectAttempts.Should().Be(19);
        merged.MasteryScore.Should().BeGreaterThanOrEqualTo(0.9f, "ancient mastery must not be lost");
        merged.CurrentStreak.Should().BeGreaterThanOrEqualTo(10f);
        merged.ProductionInStreak.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task Merge_LegacyAggregateOnly_PreservesReviewSchedule()
    {
        // Arrange — both rows predate per-attempt context tracking, so there are no contexts to replay.
        // The duplicate carries a real spaced-repetition schedule (due date, interval, ease). Replay would
        // reset these to defaults (NextReviewDate=null), making the merged word never due for review again,
        // so the merge must carry the schedule forward.
        var keeper = SeedWord("바다", "sea");
        var dup = SeedWord("바다 ", "sea (dup)");
        var dueDate = DateTime.UtcNow.AddDays(9);

        SeedAggregateOnlyProgress(keeper, UserA,
            masteryScore: 0.5f, totalAttempts: 5, correctAttempts: 4,
            currentStreak: 2f, productionInStreak: 1);
        SeedAggregateOnlyProgress(dup, UserA,
            masteryScore: 0.88f, totalAttempts: 30, correctAttempts: 27,
            currentStreak: 12f, productionInStreak: 8,
            nextReviewDate: dueDate, reviewInterval: 14, easeFactor: 2.6f);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — the merged keeper carries the duplicate's real review cadence forward.
        var merged = GetProgress(keeper, UserA)!;
        merged.NextReviewDate.Should().NotBeNull("a merged word must keep a review date so it resurfaces");
        merged.NextReviewDate!.Value.Should().BeCloseTo(dueDate, TimeSpan.FromSeconds(1));
        merged.ReviewInterval.Should().Be(14);
        merged.EaseFactor.Should().BeApproximately(2.6f, 0.0001f);
        merged.IsDueForReview.Should().BeFalse("the carried-over due date is in the future");
    }

    [Fact]
    public async Task Merge_DeclaredFamiliarDuplicate_NeverQuizzed_PreservesScheduleAndDeclaration()
    {
        // Arrange — the user marked the DUPLICATE "Familiar" (a high-value "I know this" signal) but never
        // quizzed it, so it carries a 14-day grace-period schedule with ZERO attempts. The keeper was never
        // touched. summedTotal == 0 here, so the schedule restore must gate on the schedule's existence
        // rather than on attempt count, otherwise the grace cadence is lost and the word goes dormant
        // (NextReviewDate=null => never due) after the grace window — stuck Familiar with no verification probe.
        var keeper = SeedWord("우유", "milk");
        var dup = SeedWord("우유 ", "milk (dup)");
        var dueDate = DateTime.UtcNow.AddDays(14);

        SeedDeclaredFamiliarProgress(dup, UserA, dueDate);
        GetProgress(keeper, UserA).Should().BeNull("keeper was never practiced or declared");

        // Act
        await MergeAsync(keeper, dup);

        // Assert — the keeper carries BOTH the declaration and the review cadence forward.
        GetProgress(dup, UserA).Should().BeNull();
        var merged = GetProgress(keeper, UserA)!;
        merged.IsUserDeclared.Should().BeTrue("a user-declared Familiar status is high-value signal");
        merged.IsFamiliar.Should().BeTrue("declaration + Pending verification must survive the merge");
        merged.NextReviewDate.Should().NotBeNull("the grace-period review date must survive the merge");
        merged.NextReviewDate!.Value.Should().BeCloseTo(dueDate, TimeSpan.FromSeconds(1));
        merged.ReviewInterval.Should().Be(30);
    }

    [Fact]
    public async Task Merge_WhenOnlyDuplicatePracticed_CreatesKeeperProgress()
    {
        // Arrange — the keeper word was never practiced by this user; only the duplicate was.
        var t = DateTime.UtcNow.AddDays(-3);
        var keeper = SeedWord("의자", "chair");
        var dup = SeedWord("의자 ", "chair (dup)");

        var dupProg = SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
            new Ctx(t.AddMinutes(2), Correct: true),
        });
        GetProgress(keeper, UserA).Should().BeNull("keeper has no progress yet");

        var oracle = BuildOracle(keeper, UserA, dupProg);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — a keeper progress record is created carrying the duplicate's history.
        GetProgress(dup, UserA).Should().BeNull();
        var merged = GetProgress(keeper, UserA);
        merged.Should().NotBeNull();
        merged!.TotalAttempts.Should().Be(2);
        merged.MasteryScore.Should().BeGreaterThan(0f);
        merged.MasteryScore.Should().BeApproximately(oracle.MasteryScore, 0.0001f);
    }

    [Fact]
    public async Task LivePath_And_Replay_ProduceIdenticalAggregate()
    {
        // This guards the "single source of truth" guarantee: the merge recalculates by replaying
        // contexts through VocabularyMasteryCalculator, so a replay of the live path's own contexts
        // must reproduce the live path's aggregate exactly. If the two ever diverge, merge math is wrong.
        using var scope = _fixture.ServiceProvider.CreateScope();
        var progressRepo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var contextRepo = new VocabularyLearningContextRepository(
            _fixture.ServiceProvider,
            loggerFactory.CreateLogger<VocabularyLearningContextRepository>());
        var service = new VocabularyProgressService(
            progressRepo,
            contextRepo,
            loggerFactory.CreateLogger<VocabularyProgressService>(),
            _fixture.ServiceProvider);

        var wordId = SeedWord("사랑", "love");

        var inputs = new[]
        {
            (Correct: true,  Mode: "MultipleChoice"),
            (Correct: true,  Mode: "Text"),
            (Correct: false, Mode: "Text"),
            (Correct: true,  Mode: "Text"),
            (Correct: true,  Mode: "MultipleChoice"),
        };

        VocabularyProgress live = null!;
        foreach (var (correct, mode) in inputs)
        {
            live = await service.RecordAttemptAsync(new VocabularyAttempt
            {
                VocabularyWordId = wordId,
                UserId = UserA,
                WasCorrect = correct,
                DifficultyWeight = 1.0f,
                InputMode = mode,
                Activity = "VocabularyQuiz",
                ContextType = "Isolated"
            });
            await Task.Delay(3); // ensure distinct LearnedAt timestamps for a stable replay order
        }

        // Replay the live path's own persisted contexts into a fresh aggregate.
        List<VocabularyLearningContext> contexts;
        using (var readScope = _fixture.ServiceProvider.CreateScope())
        {
            var db = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            contexts = db.VocabularyLearningContexts.AsNoTracking()
                .Where(c => c.VocabularyProgressId == live.Id)
                .ToList();
        }

        contexts.Should().HaveCount(inputs.Length, "every attempt persists exactly one context");

        var replay = new VocabularyProgress { Id = "replay", VocabularyWordId = wordId, UserId = UserA };
        VocabularyMasteryCalculator.RecalculateInto(replay, contexts, out _);

        replay.TotalAttempts.Should().Be(live.TotalAttempts);
        replay.CorrectAttempts.Should().Be(live.CorrectAttempts);
        replay.ProductionInStreak.Should().Be(live.ProductionInStreak);
        replay.CurrentStreak.Should().BeApproximately(live.CurrentStreak, 0.0001f);
        replay.MasteryScore.Should().BeApproximately(live.MasteryScore, 0.0001f);
    }

    #region Helpers

    private string SeedWord(string target, string native)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = target,
            NativeLanguageTerm = native,
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.VocabularyWords.Add(word);
        db.SaveChanges();
        return word.Id;
    }

    private void SeedUserProfile(string userId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (db.UserProfiles.Find(userId) != null)
            return;
        db.UserProfiles.Add(new UserProfile
        {
            Id = userId,
            Name = $"User {userId}",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = 20,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Seeds a progress row whose aggregate is made self-consistent by replaying the given contexts —
    /// exactly what the live path would have produced for that history.
    /// </summary>
    private VocabularyProgress SeedProgress(string wordId, string userId, IEnumerable<Ctx> ctxs)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var progress = new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = userId
        };

        var ctxList = ctxs.Select(c => new VocabularyLearningContext
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyProgressId = progress.Id,
            Activity = c.Activity,
            InputMode = c.Passive ? "Passive" : c.InputMode,
            ContextType = c.Passive ? "Exposure" : "Isolated",
            WasCorrect = c.Correct,
            DifficultyScore = 1.0f,
            LearnedAt = c.At,
            CreatedAt = c.At,
            UpdatedAt = c.At
        }).ToList();

        VocabularyMasteryCalculator.RecalculateInto(progress, ctxList, out _);
        progress.FirstSeenAt = ctxList.Count > 0 ? ctxList.Min(c => c.LearnedAt) : DateTime.UtcNow;
        progress.CreatedAt = progress.FirstSeenAt;

        db.VocabularyProgresses.Add(progress);
        db.VocabularyLearningContexts.AddRange(ctxList);
        db.SaveChanges();
        return progress;
    }

    /// <summary>Seeds an aggregate-only progress row (no contexts), simulating pre-context-tracking history.</summary>
    private void SeedAggregateOnlyProgress(
        string wordId, string userId, float masteryScore, int totalAttempts,
        int correctAttempts, float currentStreak, int productionInStreak,
        DateTime? nextReviewDate = null, int reviewInterval = 1, float easeFactor = 2.5f)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = userId,
            MasteryScore = masteryScore,
            TotalAttempts = totalAttempts,
            CorrectAttempts = correctAttempts,
            CurrentStreak = currentStreak,
            ProductionInStreak = productionInStreak,
            NextReviewDate = nextReviewDate,
            ReviewInterval = reviewInterval,
            EaseFactor = easeFactor,
            FirstSeenAt = DateTime.UtcNow.AddDays(-60),
            LastPracticedAt = DateTime.UtcNow.AddDays(-20),
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            UpdatedAt = DateTime.UtcNow.AddDays(-20)
        });
        db.SaveChanges();
    }

    /// <summary>Seeds a user-declared "Familiar" progress row with a grace-period schedule but ZERO attempts,
    /// mirroring SetUserDeclaredStatusAsync's Familiar branch (the user said "I know this" without quizzing).</summary>
    private void SeedDeclaredFamiliarProgress(string wordId, string userId, DateTime nextReviewDate)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = userId,
            IsUserDeclared = true,
            UserDeclaredAt = DateTime.UtcNow.AddDays(-2),
            VerificationState = VerificationStatus.Pending,
            TotalAttempts = 0,
            CorrectAttempts = 0,
            NextReviewDate = nextReviewDate,
            ReviewInterval = 30,
            EaseFactor = 2.5f,
            FirstSeenAt = DateTime.UtcNow.AddDays(-2),
            LastPracticedAt = DateTime.UtcNow.AddDays(-2),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        });
        db.SaveChanges();
    }

    /// <summary>Builds the "as if always one word" oracle by replaying every source's contexts.</summary>
    private VocabularyProgress BuildOracle(string keeperWordId, string userId, params VocabularyProgress[] sources)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ids = sources.Select(s => s.Id).ToList();
        var contexts = db.VocabularyLearningContexts.AsNoTracking()
            .Where(c => ids.Contains(c.VocabularyProgressId))
            .ToList();

        var oracle = new VocabularyProgress { Id = "oracle", VocabularyWordId = keeperWordId, UserId = userId };
        VocabularyMasteryCalculator.RecalculateInto(oracle, contexts, out _);
        return oracle;
    }

    private async Task<int> MergeAsync(string keeperWordId, params string[] dupIds)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();
        return await repo.MergeVocabularyWordsAsync(keeperWordId, dupIds.ToList());
    }

    private VocabularyProgress? GetProgress(string wordId, string userId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyProgresses.AsNoTracking()
            .FirstOrDefault(p => p.VocabularyWordId == wordId && p.UserId == userId);
    }

    private int CountProgressFor(string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyProgresses.AsNoTracking().Count(p => p.VocabularyWordId == wordId);
    }

    private int CountAllContexts()
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyLearningContexts.AsNoTracking().Count();
    }

    private int CountContextsFor(string progressId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyLearningContexts.AsNoTracking().Count(c => c.VocabularyProgressId == progressId);
    }

    private bool WordExists(string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyWords.AsNoTracking().Any(w => w.Id == wordId);
    }

    #endregion
}
