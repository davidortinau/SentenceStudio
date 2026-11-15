using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.PlanGeneration;
using Xunit;

namespace SentenceStudio.UnitTests.Integration;

/// <summary>
/// Known words (docs/known-words-plan.md, Captain 2026-11-14): the learner's marks, the Learning
/// mode, and practice, on real SQLite. The mark, the score and the mode are separate: a mark never
/// changes the score, and switching the mode writes no progress row.
/// </summary>
public class KnownWordsTests : IClassFixture<PlanGenerationTestFixture>
{
    private const string UserA = PlanGenerationTestFixture.TestUserId;
    private const string UserB = "known-words-user-b";
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly PlanGenerationTestFixture _fixture;
    private readonly VocabularyProgressService _service;
    private readonly VocabularyProgressRepository _repo;

    public KnownWordsTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile();
        var scope = fixture.ServiceProvider.CreateScope();
        _repo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();
        var loggers = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        _service = new VocabularyProgressService(_repo,
            new VocabularyLearningContextRepository(fixture.ServiceProvider,
                loggers.CreateLogger<VocabularyLearningContextRepository>()),
            loggers.CreateLogger<VocabularyProgressService>(), fixture.ServiceProvider);
    }

    [Fact]
    public async Task MarkKnown_WritesOnlyTheMark_AndTheWordIsKnownWithNoPractice()
    {
        var word = SeedWords(1)[0];

        var marked = await _service.SetLearnerMarkAsync([word], VocabularyLearnerAction.MarkKnown, UserA);

        marked.Changed.Should().Be(1);
        var row = Row(word);
        row.VerificationState.Should().Be(VerificationStatus.Confirmed);
        row.IsUserDeclared.Should().BeTrue();
        row.UserDeclaredAt.Should().NotBeNull();
        (row.MasteryScore, row.TotalAttempts, row.CurrentStreak).Should().Be((0f, 0, 0f), "a mark never writes the score");
        VocabularyKnownRule.Status(row, LearningMode.LearnerLed).Should().Be(LearningStatus.Known);
        VocabularyKnownRule.Source(row, LearningMode.LearnerLed).Should().Be(VocabularyKnownSource.Learner);

        // Never scheduled: not new, not due, and the dashboard counts it Known only.
        var counts = await _repo.GetVocabSummaryCountsAsync(UserA);
        (counts.New, counts.Learning, counts.Familiar, counts.Review, counts.Known).Should().Be((0, 0, 0, 0, 1));
    }

    [Fact]
    public async Task AMarkedWord_StaysKnownAfterAMiss_AndTheMissIsStillScored()
    {
        var word = SeedWords(1)[0];
        await _service.SetLearnerMarkAsync([word], VocabularyLearnerAction.MarkKnown, UserA);

        var after = await _service.RecordAttemptAsync(Attempt(word, correct: false));

        after.VerificationState.Should().Be(VerificationStatus.Confirmed);
        VocabularyKnownRule.IsKnown(after, LearningMode.LearnerLed).Should().BeTrue();
        (after.TotalAttempts, after.CorrectAttempts).Should().Be((1, 0), "the miss is saved");
        (await _repo.GetDueVocabCountAsync(DateTime.UtcNow.AddYears(1), UserA)).Should().Be(0, "a Known word is never due");
    }

    [Fact]
    public async Task SwitchingTheMode_WritesNoProgressRow_AndOnlyChangesHowMarksAreRead()
    {
        var words = SeedWords(2);
        await _service.SetLearnerMarkAsync(words, VocabularyLearnerAction.MarkKnown, UserA);
        var before = Snapshot();

        SetMode(UserA, LearningMode.PracticeLed);

        Snapshot().Should().Be(before, "the marks stay exactly as they are");
        (await _service.GetLearningModeAsync(UserA)).Should().Be(LearningMode.PracticeLed);
        var counts = await _repo.GetVocabSummaryCountsAsync(UserA);
        (counts.New, counts.Known).Should().Be((2, 0), "in Practice-led a marked word is judged by its score");
        (await _service.SetLearnerMarkAsync(words, VocabularyLearnerAction.MarkKnown, UserA))
            .Should().Be(VocabularyMarkResult.Refused("Vocabulary_MarkKnownPracticeLed"));

        SetMode(UserA, LearningMode.LearnerLed);

        Snapshot().Should().Be(before);
        (await _repo.GetVocabSummaryCountsAsync(UserA)).Known.Should().Be(2, "switching back brings the marks back");
    }

    [Fact]
    public async Task PracticeAgain_StartsAtTypingLevel_AndTheWordGrowsBackToKnownAndStays()
    {
        var word = SeedWords(1)[0];
        await _service.SetLearnerMarkAsync([word], VocabularyLearnerAction.MarkKnown, UserA);

        await _service.SetLearnerMarkAsync([word], VocabularyLearnerAction.PracticeAgain, UserA);

        var reopened = Row(word);
        reopened.VerificationState.Should().Be(VerificationStatus.Reopened);
        (reopened.MasteryScore, reopened.CurrentStreak, reopened.ProductionInStreak, reopened.ReviewInterval)
            .Should().Be((VocabularyKnownMarks.PracticeAgainMastery, 0f, 0, 1));
        reopened.NextReviewDate.Should().NotBeNull("it is due at once");
        VocabularyKnownRule.Status(reopened, LearningMode.LearnerLed).Should().Be(LearningStatus.Learning);
        (await _repo.GetDueVocabCountAsync(DateTime.UtcNow.AddMinutes(1), UserA)).Should().Be(1);

        // It grows back through practice, by the same score rule as any word.
        VocabularyProgress grown = reopened;
        for (var i = 0; i < 12 && !VocabularyKnownRule.IsKnownByScore(grown); i++)
            grown = await _service.RecordAttemptAsync(Attempt(word, correct: true));
        VocabularyKnownRule.IsKnownByScore(grown).Should().BeTrue();
        grown.VerificationState.Should().Be(VerificationStatus.KnownByPractice);
        grown.UserDeclaredAt.Should().BeAfter(reopened.UserDeclaredAt!.Value, "growing back takes the answer's time");

        // And then stays Known through a miss.
        var missed = await _service.RecordAttemptAsync(Attempt(word, correct: false));
        VocabularyKnownRule.IsKnown(missed, LearningMode.LearnerLed).Should().BeTrue();
    }

    [Fact]
    public async Task StartOver_ResetsTheScoreToZero_AndKeepsTheHistory()
    {
        var word = SeedWords(1)[0];
        for (var i = 0; i < 3; i++)
            await _service.RecordAttemptAsync(Attempt(word, correct: true));

        await _service.SetLearnerMarkAsync([word], VocabularyLearnerAction.StartOver, UserA);

        var row = Row(word);
        row.VerificationState.Should().Be(VerificationStatus.StartedOver);
        (row.MasteryScore, row.CurrentStreak, row.NextReviewDate).Should().Be((0f, 0f, null));
        row.TotalAttempts.Should().Be(3, "history and counts are kept");
    }

    [Fact]
    public async Task InPracticeLed_AKnownWordIsNotStamped_AndAMissCanDropIt()
    {
        var word = SeedWords(1)[0];
        SetMode(UserA, LearningMode.PracticeLed);
        VocabularyProgress row = null!;
        for (var i = 0; i < 12; i++)
            row = await _service.RecordAttemptAsync(Attempt(word, correct: true));
        VocabularyKnownRule.IsKnown(row, LearningMode.PracticeLed).Should().BeTrue();
        row.VerificationState.Should().Be(VerificationStatus.None, "nothing is stamped in Practice-led");

        for (var i = 0; i < 4; i++)
            row = await _service.RecordAttemptAsync(Attempt(word, correct: false));

        VocabularyKnownRule.IsKnown(row, LearningMode.PracticeLed).Should().BeFalse();
    }

    [Fact]
    public async Task LiveScoring_AndAReplayOfTheSameHistory_AgreeAcrossAPracticeAgain()
    {
        var word = SeedWords(1)[0];
        for (var i = 0; i < 5; i++)
            await _service.RecordAttemptAsync(Attempt(word, correct: true));
        await _service.SetLearnerMarkAsync([word], VocabularyLearnerAction.PracticeAgain, UserA);
        await _service.RecordAttemptAsync(Attempt(word, correct: true));
        await _service.RecordAttemptAsync(Attempt(word, correct: false));
        var live = Row(word);

        using var scope = _fixture.ServiceProvider.CreateScope();
        var contexts = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyLearningContexts.AsNoTracking().Where(c => c.VocabularyProgressId == live.Id).ToList();
        var replay = new VocabularyProgress { Id = live.Id, VocabularyWordId = word, UserId = UserA };
        VocabularyMasteryCalculator.RecalculateInto(replay, contexts, VocabularyKnownMarks.ResetOf(live), out _);

        replay.MasteryScore.Should().BeApproximately(live.MasteryScore, 0.0001f);
        (replay.CurrentStreak, replay.ProductionInStreak, replay.ReviewInterval, replay.TotalAttempts)
            .Should().Be((live.CurrentStreak, live.ProductionInStreak, live.ReviewInterval, live.TotalAttempts));
    }

    [Fact]
    public async Task Marks_ActOnlyOnTheLearnersOwnWords()
    {
        _fixture.SeedUserProfile(userId: UserB);
        var mine = SeedWords(1)[0];
        var theirs = SeedWords(1, UserB)[0];

        var marked = await _service.SetLearnerMarkAsync([mine, theirs], VocabularyLearnerAction.MarkKnown, UserA);

        marked.Changed.Should().Be(1);
        Rows(theirs).Should().BeEmpty("another learner's word gets no progress row and no mark");
        (await _service.SetLearnerMarkAsync([theirs], VocabularyLearnerAction.StartOver, UserA))
            .Should().Be(VocabularyMarkResult.Refused("Vocabulary_MarkRefusedNotYours"));
        // Each learner's own mode is read for their own words.
        SetMode(UserB, LearningMode.PracticeLed);
        (await _service.GetLearningModeAsync(UserA)).Should().Be(LearningMode.LearnerLed);
        (await _repo.GetVocabSummaryCountsAsync(UserA)).Known.Should().Be(1);
    }

    [Fact]
    public void Merge_AMissOnAnotherDevice_CannotUndoAMarkMadeElsewhere()
    {
        var stored = new VocabularyProgress { Id = "p", UserId = UserA, VocabularyWordId = "w", TotalAttempts = 2, CorrectAttempts = 2 };
        VocabularyKnownMarks.MarkKnown(stored, T0.AddHours(2));
        // The device's upload counts the stored attempts plus a miss, and knows of no mark.
        var incoming = new VocabularyProgress { Id = "p", UserId = UserA, VocabularyWordId = "w",
            TotalAttempts = 3, CorrectAttempts = 2, MasteryScore = 0.1f };

        VocabularyProgressConcurrentMerge.TryMerge(stored, incoming, [], [], T0.AddHours(3), out var merged);

        merged.VerificationState.Should().Be(VerificationStatus.Confirmed);
        VocabularyKnownRule.IsKnown(merged, LearningMode.LearnerLed).Should().BeTrue();
        merged.TotalAttempts.Should().Be(3);
    }

    [Fact]
    public void Merge_PracticeAgainOnOneDevice_IsNotUndoneByAStaleKnownRowFromAnother()
    {
        // The web reopened the word at T0+2h; the stored row carries the reset.
        var stored = new VocabularyProgress { Id = "p", UserId = UserA, VocabularyWordId = "w",
            TotalAttempts = 10, CorrectAttempts = 10, MasteryScore = 1f, CurrentStreak = 10, ProductionInStreak = 4, ReviewInterval = 60 };
        VocabularyKnownMarks.PracticeAgain(stored, T0.AddHours(2));
        // A device that had not synced still holds it Known, stamps it, and answers once more.
        var incoming = new VocabularyProgress { Id = "p", UserId = UserA, VocabularyWordId = "w",
            TotalAttempts = 11, CorrectAttempts = 11, MasteryScore = 1f, CurrentStreak = 11, ProductionInStreak = 5,
            ReviewInterval = 60, VerificationState = VerificationStatus.KnownByPractice };
        var answer = new VocabularyLearningContext { Id = "c11", VocabularyProgressId = "p", Activity = "VocabularyQuiz",
            InputMode = "Text", WasCorrect = true, LearnedAt = T0.AddHours(3), CreatedAt = T0.AddHours(3) };

        var changed = VocabularyProgressConcurrentMerge.TryMerge(stored, incoming, [], [answer], T0.AddHours(4), out var merged);

        changed.Should().BeTrue("the upload's score predates the reopen, so it is not kept whole");
        merged.VerificationState.Should().Be(VerificationStatus.Reopened, "a learner's action beats an automatic stamp");
        VocabularyKnownRule.IsKnown(merged, LearningMode.LearnerLed).Should().BeFalse();
        merged.MasteryScore.Should().BeLessThan(VocabularyKnownRule.HighConfidenceMasteryFloor);
        merged.TotalAttempts.Should().Be(11, "no count is lost");
    }

    [Fact]
    public void Merge_AStampSurvives_WhenTheOtherSideHasNoMark()
    {
        var stored = new VocabularyProgress { Id = "p", UserId = UserA, VocabularyWordId = "w", TotalAttempts = 2, CorrectAttempts = 2 };
        var incoming = new VocabularyProgress { Id = "p", UserId = UserA, VocabularyWordId = "w", TotalAttempts = 3,
            CorrectAttempts = 2, VerificationState = VerificationStatus.KnownByPractice };

        VocabularyProgressConcurrentMerge.TryMerge(incoming, stored, [], [], T0, out var merged);

        merged.VerificationState.Should().Be(VerificationStatus.KnownByPractice);
    }

    private List<string> SeedWords(int count, string? owner = null)
    {
        var resource = _fixture.SeedResource(vocabWordCount: count, userProfileId: owner);
        return _fixture.GetResourceVocabularyWordIds(resource.Id);
    }

    private static VocabularyAttempt Attempt(string wordId, bool correct) => new()
    {
        VocabularyWordId = wordId, UserId = UserA, Activity = "VocabularyQuiz", InputMode = "Text",
        WasCorrect = correct, ContextType = "Isolated", ResponseTimeMs = 1000
    };

    private VocabularyProgress Row(string wordId) => Rows(wordId).Single();

    private List<VocabularyProgress> Rows(string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().VocabularyProgresses
            .AsNoTracking().Where(p => p.VocabularyWordId == wordId).ToList();
    }

    private string Snapshot()
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        return string.Join("\n", scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().VocabularyProgresses
            .AsNoTracking().OrderBy(p => p.Id).ToList()
            .Select(p => $"{p.Id}|{p.VerificationState}|{p.IsUserDeclared}|{p.UserDeclaredAt:O}|{p.MasteryScore}|{p.UpdatedAt:O}"));
    }

    private void SetMode(string userId, LearningMode mode)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Single(p => p.Id == userId).LearningMode = mode;
        db.SaveChanges();
    }
}
