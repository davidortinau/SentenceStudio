using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Data;
using SentenceStudio.Services.Vocabulary;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services.Vocabulary;

/// <summary>
/// The Aspire Npgsql registration installs a retrying execution strategy, and EF refuses a
/// user-initiated transaction under one unless the transaction runs inside
/// <c>CreateExecutionStrategy().ExecuteAsync(...)</c>.
/// </summary>
/// <remarks>
/// This shipped once: the batch commit threw
/// "The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support
/// user-initiated transactions" on the first real PostgreSQL run, while every SQLite test passed —
/// SQLite has no retrying strategy, so the default one never objects. The test below configures a
/// retrying strategy on SQLite so the failure mode is reproducible without a PostgreSQL server.
/// </remarks>
public class VocabularyPartOfSpeechBackfillRetryStrategyTests
{
    [Fact]
    public async Task CommitsUnderARetryingExecutionStrategy()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection, o => o.ExecutionStrategy(dependencies => new AlwaysRetryingExecutionStrategy(dependencies)))
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (var seed = new ApplicationDbContext(options))
        {
            seed.Database.EnsureCreated();
            seed.VocabularyWords.Add(new VocabularyWord
            {
                Id = "w-1",
                TargetLanguageTerm = "책",
                Language = "Korean",
                LexicalUnitType = LexicalUnitType.Word,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            seed.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = "p-1",
                VocabularyWordId = "w-1",
                UserId = PartOfSpeechBackfillHarness.OwnerId
            });
            seed.SaveChanges();
        }

        using var db = new ApplicationDbContext(options);
        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");

        var service = new VocabularyPartOfSpeechBackfillService(
            db,
            chat,
            Options.Create(PartOfSpeechBackfillHarness.EnabledFor()),
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace))
                .CreateLogger<VocabularyPartOfSpeechBackfillService>());

        var report = await service.RunAsync();

        report.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.Completed);
        report.WordsUpdated.Should().Be(1);

        using var verify = new ApplicationDbContext(options);
        verify.VocabularyWords.AsNoTracking().Single(w => w.Id == "w-1")
            .PartOfSpeech.Should().Be(VocabularyPartOfSpeech.Noun);
    }

    /// <summary>
    /// A strategy that reports <c>RetriesOnFailure</c>, which is the property EF checks before
    /// rejecting a transaction it does not own. It never actually retries — the point is to
    /// reproduce the guard, not the retry.
    /// </summary>
    private sealed class AlwaysRetryingExecutionStrategy : ExecutionStrategy
    {
        public AlwaysRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
        {
        }

        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
