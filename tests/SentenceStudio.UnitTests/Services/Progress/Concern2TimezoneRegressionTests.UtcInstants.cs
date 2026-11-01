using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services.Progress;

/// <summary>
/// UTC instant contract for learning-progress timestamps, in US Central and Korea.
/// Progress timestamps (NextReviewDate, LastPracticedAt, LearnedAt, ...) are UTC instants on
/// SQLite and PostgreSQL. "Due today" and streaks use the learner's LOCAL calendar day,
/// expressed as the UTC instant that opens that day.
/// </summary>
public partial class Concern2TimezoneRegressionTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

    [Fact]
    public void DueCutoff_UsCentral_IsTheUtcInstantOpeningTheLocalDay()
    {
        // 2026-10-31 11:00 CDT (UTC-5).
        var context = new PlanDateContext(Chicago, () => new DateTime(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc));

        var cutoff = context.DueCutoffUtc();

        cutoff.Should().Be(new DateTime(2026, 10, 31, 5, 0, 0, DateTimeKind.Utc));
        cutoff.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void DueCutoff_Korea_IsTheUtcInstantOpeningTheLocalDay()
    {
        // 2026-11-01 01:00 KST (UTC+9): the local day has already rolled over.
        var context = new PlanDateContext(Seoul, () => new DateTime(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc));

        context.UserLocalDate.Should().Be(new DateOnly(2026, 11, 1));
        context.DueCutoffUtc().Should().Be(new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("America/Chicago")]
    [InlineData("Asia/Seoul")]
    public async Task DueQuery_OnSqlite_ComparesUtcInstantsAgainstLocalDayCutoff(string zoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var context = new PlanDateContext(zone, () => new DateTime(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc));
        var cutoff = context.DueCutoffUtc();

        await using var store = await UtcStore.CreateAsync();
        store.Db.VocabularyProgresses.AddRange(
            store.Progress("due-before-cutoff", cutoff.AddMinutes(-1)),
            // A writer that still passes a Local-kind value stores the same instant.
            store.Progress("due-local-kind", cutoff.AddMinutes(-2).ToLocalTime()),
            store.Progress("not-due-after-cutoff", cutoff.AddMinutes(1)));
        await store.Db.SaveChangesAsync();
        store.Db.ChangeTracker.Clear();

        var due = await store.Db.VocabularyProgresses
            .Where(p => p.NextReviewDate <= cutoff)
            .Select(p => p.Id)
            .ToListAsync();

        due.Should().BeEquivalentTo(new[] { "due-before-cutoff", "due-local-kind" });
    }

    [Fact]
    public async Task SqliteRoundTrip_StoresUtcWallClockText_AndReadsKindUtc()
    {
        var instant = new DateTime(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc);
        await using var store = await UtcStore.CreateAsync();
        var progress = store.Progress("round-trip", instant);
        progress.LastPracticedAt = instant.ToLocalTime(); // Local kind on purpose
        store.Db.VocabularyProgresses.Add(progress);
        store.Db.VocabularyLearningContexts.Add(new VocabularyLearningContext
        {
            Id = "ctx-round-trip", VocabularyProgressId = progress.Id,
            LearnedAt = instant, CreatedAt = instant, UpdatedAt = instant,
        });
        await store.Db.SaveChangesAsync();
        store.Db.ChangeTracker.Clear();

        // The text format is unchanged (zone-less EF SQLite text) and holds the UTC wall clock,
        // which is what CoreSync uploads and PostgreSQL timestamptz reads as UTC.
        (await store.ScalarAsync("SELECT NextReviewDate FROM VocabularyProgress WHERE Id = 'round-trip'"))
            .Should().Be("2026-10-31 16:00:00");
        (await store.ScalarAsync("SELECT LastPracticedAt FROM VocabularyProgress WHERE Id = 'round-trip'"))
            .Should().Be("2026-10-31 16:00:00");
        (await store.ScalarAsync("SELECT LearnedAt FROM VocabularyLearningContext WHERE Id = 'ctx-round-trip'"))
            .Should().Be("2026-10-31 16:00:00");

        var read = await store.Db.VocabularyProgresses.SingleAsync(p => p.Id == "round-trip");
        read.NextReviewDate.Should().Be(instant);
        read.NextReviewDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
        read.LastPracticedAt.Should().Be(instant);
        read.LastPracticedAt.Kind.Should().Be(DateTimeKind.Utc);
        var context = await store.Db.VocabularyLearningContexts.SingleAsync(c => c.Id == "ctx-round-trip");
        context.LearnedAt.Should().Be(instant);
        context.LearnedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task HistoricZonelessRow_IsReadAsStored_NotRewritten()
    {
        // A row written before the fix by a Korean device holds local wall-clock text. Its true
        // offset is unknowable once it may have synced, so it is never rewritten; it reads back
        // with the same wall clock labeled UTC (the documented residual skew).
        await using var store = await UtcStore.CreateAsync();
        store.Db.VocabularyProgresses.Add(
            store.Progress("historic", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        await store.Db.SaveChangesAsync();
        await store.ExecuteAsync(
            "UPDATE VocabularyProgress SET NextReviewDate = '2026-10-31 09:00:00' WHERE Id = 'historic'");
        store.Db.ChangeTracker.Clear();

        var read = await store.Db.VocabularyProgresses.SingleAsync(p => p.Id == "historic");

        read.NextReviewDate.Should().Be(new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc));
        read.NextReviewDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
        (await store.ScalarAsync("SELECT NextReviewDate FROM VocabularyProgress WHERE Id = 'historic'"))
            .Should().Be("2026-10-31 09:00:00");
    }

    [Fact]
    public void Streak_Korea_CountsLocalDays_NotUtcDays()
    {
        // 2026-10-31 12:00 KST. Activities at 2026-10-31 08:30 KST and 2026-10-30 10:00 KST fall
        // on two local days but on ONE UTC day (2026-10-30).
        var context = new PlanDateContext(Seoul, () => new DateTime(2026, 10, 31, 3, 0, 0, DateTimeKind.Utc));
        var activities = new[]
        {
            new DateTime(2026, 10, 30, 23, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc),
        };

        var streak = SentenceStudio.Services.Progress.ProgressService.ComputeStreak(activities, context);

        streak.CurrentStreak.Should().Be(2);
        streak.LastPracticeDate.Should().Be(new DateTime(2026, 10, 31));
    }

    [Fact]
    public void Streak_UsCentral_CountsLocalDays_NotUtcDays()
    {
        // 2026-10-31 11:00 CDT. Activities at 2026-10-30 22:00 CDT and 2026-10-30 10:00 CDT are
        // ONE local day although they straddle UTC midnight.
        var context = new PlanDateContext(Chicago, () => new DateTime(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc));
        var activities = new[]
        {
            new DateTime(2026, 10, 31, 3, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc),
        };

        var streak = SentenceStudio.Services.Progress.ProgressService.ComputeStreak(activities, context);

        streak.CurrentStreak.Should().Be(1);
        streak.LastPracticeDate.Should().Be(new DateTime(2026, 10, 30));
    }

    [Fact]
    public void IsDueForReview_ComparesUtcInstants()
    {
        new VocabularyProgress { NextReviewDate = DateTime.UtcNow.AddMinutes(-1) }
            .IsDueForReview.Should().BeTrue();
        new VocabularyProgress { NextReviewDate = DateTime.UtcNow.AddMinutes(1) }
            .IsDueForReview.Should().BeFalse();
    }

    private sealed class UtcStore : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private UtcStore(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public ApplicationDbContext Db { get; }

        public static async Task<UtcStore> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTime.UtcNow;
            db.UserProfiles.Add(new UserProfile
            {
                Id = "utc-user", Name = "UTC", NativeLanguage = "English",
                TargetLanguage = "Korean", CreatedAt = now,
            });
            await db.SaveChangesAsync();
            return new UtcStore(connection, db);
        }

        /// <summary>One word per progress row: (word, user) is unique.</summary>
        public VocabularyProgress Progress(string id, DateTime nextReview)
        {
            var now = DateTime.UtcNow;
            Db.VocabularyWords.Add(new VocabularyWord
            {
                Id = $"word-{id}", TargetLanguageTerm = id, NativeLanguageTerm = id,
                CreatedAt = now, UpdatedAt = now,
            });
            return new VocabularyProgress
            {
                Id = id, UserId = "utc-user", VocabularyWordId = $"word-{id}",
                TotalAttempts = 1, NextReviewDate = nextReview,
            };
        }

        public async Task<string?> ScalarAsync(string sql)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            return (await command.ExecuteScalarAsync())?.ToString();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
