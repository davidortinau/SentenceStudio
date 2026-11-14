using SentenceStudio.Shared.Models;
using SentenceStudio.Tools.ProgressCountRepair;

namespace SentenceStudio.Tools.ProgressCountRepair.Tests;

public class RepairPlannerTests
{
    private const string Owner = "owner-a";
    private static readonly DateTime Now = Utc(2026, 11, 14, 5);
    private static readonly DateTime Stored = Utc(2026, 3, 7, 11);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static VocabularyProgress Row(string id, string user, int total = 0, int correct = 0) => new()
    {
        Id = id, VocabularyWordId = $"word-{id}", UserId = user,
        TotalAttempts = total, CorrectAttempts = correct,
        FirstSeenAt = Utc(2025, 7, 18, 12), LastPracticedAt = Utc(2025, 7, 18, 12),
        CreatedAt = Utc(2025, 7, 18, 12), UpdatedAt = Stored,
    };

    private static VocabularyLearningContext Answer(string progressId, bool correct, DateTime at,
        string inputMode = "MultipleChoice") => new()
    {
        Id = Guid.NewGuid().ToString(), VocabularyProgressId = progressId, Activity = "VocabularyQuiz",
        InputMode = inputMode, WasCorrect = correct, DifficultyScore = 0.5f, LearnedAt = at, CreatedAt = at,
    };

    [Fact]
    public void AnUndercountedRowIsRebuiltFromItsAnswers()
    {
        // 곧 on the server: counted 0 attempts (last written 2026-03-07), three answers recorded.
        var row = Row("p1", Owner);
        var answers = new[]
        {
            Answer("p1", true, Utc(2026, 6, 22, 20, 54)),
            Answer("p1", true, Utc(2026, 6, 22, 20, 58)),
            Answer("p1", true, Utc(2026, 11, 14, 4, 12)),
        };

        var change = Assert.Single(RepairPlanner.Plan(Owner, [row], answers, Now));

        Assert.Equal(3, change.ActiveAnswers);
        Assert.Equal(3, change.After.TotalAttempts);
        Assert.Equal(3, change.After.CorrectAttempts);
        Assert.True(change.After.MasteryScore > 0);
        Assert.Equal(Utc(2026, 11, 14, 4, 12), change.After.LastPracticedAt);
        Assert.NotNull(change.After.NextReviewDate);
        Assert.Equal(Now, change.After.UpdatedAt);
        // The stored row is untouched; only the plan holds the new values.
        Assert.Equal(0, row.TotalAttempts);
        Assert.Contains("TotalAttempts", RepairPlanner.ChangedColumns(change.Before, change.After));
    }

    [Fact]
    public void CountedRowsExposuresAndOtherOwnersAreLeftAlone()
    {
        var counted = Row("counted", Owner, total: 2, correct: 2);
        var exposures = Row("exposures", Owner);
        var someoneElse = Row("other", "owner-b");
        var answers = new[]
        {
            Answer("counted", true, Utc(2026, 6, 1, 9)),
            Answer("counted", true, Utc(2026, 6, 2, 9)),
            Answer("exposures", true, Utc(2026, 6, 3, 9), inputMode: "Passive"),
            Answer("exposures", true, Utc(2026, 6, 4, 9), inputMode: "Passive"),
            Answer("other", true, Utc(2026, 6, 5, 9)),
            Answer("other", false, Utc(2026, 6, 6, 9)),
        };

        Assert.Empty(RepairPlanner.Plan(Owner, [counted, exposures, someoneElse], answers, Now));
        Assert.Throws<ArgumentException>(() => RepairPlanner.Plan(" ", [counted], answers, Now));
    }

    [Fact]
    public void ARepairNeverLowersCountsDatesOrADeclaration()
    {
        var row = Row("p2", Owner, total: 1, correct: 1);
        row.ExposureCount = 5;
        row.IsUserDeclared = true;
        row.UserDeclaredAt = Utc(2026, 2, 1, 8);
        row.VerificationState = VerificationStatus.Pending;
        row.LastPracticedAt = Utc(2026, 8, 1, 9);
        var answers = new[]
        {
            Answer("p2", false, Utc(2026, 6, 22, 20)),
            Answer("p2", false, Utc(2026, 6, 23, 20)),
        };

        var change = Assert.Single(RepairPlanner.Plan(Owner, [row], answers, Now));

        Assert.Equal(2, change.After.TotalAttempts);
        Assert.Equal(1, change.After.CorrectAttempts);
        Assert.Equal(5, change.After.ExposureCount);
        Assert.Equal(Utc(2026, 8, 1, 9), change.After.LastPracticedAt);
        Assert.True(change.After.IsUserDeclared);
        Assert.Equal(VerificationStatus.Pending, change.After.VerificationState);
    }

    [Fact]
    public void MasteryFromOutsideTheHistoryIsKept()
    {
        // A known word confirmed at once: stored 1/1 at mastery 1.0; its two recorded answers
        // replay to 0.15. The repair takes the counts and keeps the mastery and its schedule.
        var row = Row("known", Owner, total: 1, correct: 1);
        row.MasteryScore = 1f;
        row.CurrentStreak = 6f;
        row.NextReviewDate = Utc(2027, 1, 5, 9);
        row.ReviewInterval = 60;
        row.EaseFactor = 2.6f;
        var answers = new[]
        {
            Answer("known", true, Utc(2026, 6, 22, 20)),
            Answer("known", true, Utc(2026, 6, 23, 20)),
        };

        var change = Assert.Single(RepairPlanner.Plan(Owner, [row], answers, Now));

        Assert.Equal(2, change.After.TotalAttempts);
        Assert.Equal(2, change.After.CorrectAttempts);
        Assert.Equal(1f, change.After.MasteryScore);
        Assert.Equal(6f, change.After.CurrentStreak);
        Assert.Equal(Utc(2027, 1, 5, 9), change.After.NextReviewDate);
        Assert.Equal(60, change.After.ReviewInterval);
        Assert.Equal(2.6f, change.After.EaseFactor);
    }

    [Fact]
    public void TheSqlChangesOnlyRepairedColumnsOfTheRowAsRead()
    {
        var row = Row("p1", Owner);
        var change = Assert.Single(RepairPlanner.Plan(Owner, [row],
            [Answer("p1", true, Utc(2026, 6, 22, 20, 54))], Now));
        var raw = new Dictionary<string, string> { ["p1"] = "2026-03-07T11:00:00.123456+00:00" };

        var sql = SqlWriter.Write(Owner, [change], raw);

        Assert.Contains("UPDATE \"VocabularyProgress\" SET ", sql);
        Assert.Contains("\"TotalAttempts\" = 1", sql);
        Assert.Contains("\"UpdatedAt\" = '2026-11-14T05:00:00.000000+00:00'", sql);
        Assert.Contains(" WHERE \"Id\" = 'p1' AND \"UserId\" = 'owner-a'"
            + " AND \"UpdatedAt\" = '2026-03-07T11:00:00.123456+00:00'::timestamptz AND \"TotalAttempts\" = 0;", sql);
        Assert.DoesNotContain("\"VocabularyWordId\" =", sql);
        Assert.DoesNotContain("\"FirstSeenAt\" =", sql);
        Assert.Equal("'o''k'", SqlWriter.Literal("o'k"));
        Assert.Equal("'-infinity'", SqlWriter.Literal(DateTime.MinValue));
        Assert.Equal("1.5", SqlWriter.Literal(1.5f));
        Assert.Equal("NULL", SqlWriter.Literal(null));
        Assert.Equal("1", SqlWriter.Literal(VerificationStatus.Pending));
    }

    [Fact]
    public void ExportsFromPostgresAndSqliteReadTheSame()
    {
        const string postgres = """
            [{"Id":"p1","VocabularyWordId":"w1","UserId":"owner-a","MasteryScore":0.25,"TotalAttempts":1,
              "CorrectAttempts":1,"IsUserDeclared":true,"VerificationState":1,"CurrentPhase":0,
              "FirstSeenAt":"-infinity","LastPracticedAt":"2026-06-22T20:54:34.54512+00:00",
              "NextReviewDate":null,"CreatedAt":"2025-07-18T12:58:51.042162+00:00",
              "UpdatedAt":"2026-03-07T11:14:40.885342+00:00"}]
            """;
        const string sqlite = """
            [{"Id":"p1","VocabularyWordId":"w1","UserId":"owner-a","MasteryScore":0.25,"TotalAttempts":1,
              "CorrectAttempts":1,"IsUserDeclared":1,"VerificationState":1,"CurrentPhase":0,
              "FirstSeenAt":"-infinity","LastPracticedAt":"2026-06-22 20:54:34.54512",
              "NextReviewDate":null,"CreatedAt":"2025-07-18 12:58:51.042162",
              "UpdatedAt":"2026-03-07 11:14:40.885342"}]
            """;

        var (fromPostgres, raw) = ExportReader.ReadProgress(postgres);
        var (fromSqlite, _) = ExportReader.ReadProgress(sqlite);

        foreach (var row in new[] { Assert.Single(fromPostgres), Assert.Single(fromSqlite) })
        {
            Assert.True(row.IsUserDeclared);
            Assert.Equal(VerificationStatus.Pending, row.VerificationState);
            Assert.Equal(DateTime.MinValue, row.FirstSeenAt);
            Assert.Equal(new DateTime(2026, 6, 22, 20, 54, 34, 545, DateTimeKind.Utc).AddTicks(1200),
                row.LastPracticedAt);
            Assert.Equal(DateTimeKind.Utc, row.UpdatedAt.Kind);
            Assert.Equal(new DateTime(2025, 7, 18, 12, 58, 51, 42, DateTimeKind.Utc).AddTicks(1620),
                row.CreatedAt);
            Assert.Null(row.NextReviewDate);
        }
        // The guard compares the timestamp exactly as PostgreSQL wrote it.
        Assert.Equal("2026-03-07T11:14:40.885342+00:00", raw["p1"]);

        var contexts = ExportReader.ReadContexts("""
            [{"Id":"c1","VocabularyProgressId":"p1","Activity":"VocabularyQuiz","InputMode":"Text",
              "WasCorrect":false,"DifficultyScore":0.5,"ResponseTimeMs":1200,"UserConfidence":null,
              "UserInput":"it's","LearnedAt":"2026-06-22T20:54:34+00:00","CorrectAnswersInContext":0,
              "CreatedAt":"2026-06-22T20:54:34+00:00","UpdatedAt":"2026-06-22T20:54:34+00:00"}]
            """);
        var context = Assert.Single(contexts);
        Assert.False(context.WasCorrect);
        Assert.Equal("it's", context.UserInput);
        Assert.Null(context.UserConfidence);
    }
}
