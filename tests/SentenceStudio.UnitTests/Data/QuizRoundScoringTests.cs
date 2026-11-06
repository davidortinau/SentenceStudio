using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Rule 6 of docs/activity-session-format.md: the one check every client uses before completing
/// a quiz run from a summary it holds no answer receipt for.
/// </summary>
public sealed class QuizRoundScoringTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string OtherOwner = "owner-b";
    private static readonly DateTime RunStartedAt = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;

    public QuizRoundScoringTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);
        _db.Database.EnsureCreated();
        _db.VocabularyWords.AddRange(
            new VocabularyWord { Id = "word-1", TargetLanguageTerm = "커피", NativeLanguageTerm = "coffee", UserProfileId = Owner },
            new VocabularyWord { Id = "word-2", TargetLanguageTerm = "주스", NativeLanguageTerm = "juice", UserProfileId = Owner });
        _db.VocabularyProgresses.AddRange(
            new VocabularyProgress { Id = "p-own-1", VocabularyWordId = "word-1", UserId = Owner },
            new VocabularyProgress { Id = "p-own-2", VocabularyWordId = "word-2", UserId = Owner },
            new VocabularyProgress { Id = "p-other-1", VocabularyWordId = "word-1", UserId = OtherOwner });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AnswerOnARoundWordAfterTheRunStarted_Counts()
    {
        Score("p-own-1", RunStartedAt.AddMinutes(3));

        (await WasScoredAsync(Owner, "word-1")).Should().BeTrue();
    }

    [Fact]
    public async Task AnswerWithinTheClockDriftBeforeTheStart_Counts_OlderOnesDoNot()
    {
        Score("p-own-1", RunStartedAt - QuizRoundScoring.ClientClockDrift + TimeSpan.FromSeconds(1));
        Score("p-own-2", RunStartedAt - QuizRoundScoring.ClientClockDrift - TimeSpan.FromSeconds(1));

        (await WasScoredAsync(Owner, "word-1")).Should().BeTrue();
        (await WasScoredAsync(Owner, "word-2")).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherLearnersAnswer_AnotherActivity_OrAnotherWord_DoNotCount()
    {
        Score("p-other-1", RunStartedAt.AddMinutes(1));
        Score("p-own-1", RunStartedAt.AddMinutes(1), activity: "Clozure");
        Score("p-own-2", RunStartedAt.AddMinutes(1));

        (await WasScoredAsync(Owner, "word-1")).Should().BeFalse();
    }

    [Fact]
    public async Task NoLearnerOrNoWords_IsNotScored()
    {
        Score("p-own-1", RunStartedAt.AddMinutes(1));

        (await WasScoredAsync("", "word-1")).Should().BeFalse();
        (await WasScoredAsync(Owner)).Should().BeFalse();
        (await WasScoredAsync(Owner, " ", "")).Should().BeFalse();
    }

    private Task<bool> WasScoredAsync(string userId, params string[] words) =>
        QuizRoundScoring.WasScoredSinceAsync(_db, userId, words, RunStartedAt);

    private void Score(string progressId, DateTime learnedAt, string activity = QuizRoundScoring.Activity)
    {
        _db.VocabularyLearningContexts.Add(new VocabularyLearningContext
        {
            VocabularyProgressId = progressId,
            Activity = activity,
            WasCorrect = true,
            LearnedAt = learnedAt,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
