// Regression tests for RepairTaintedVocabularyProgressAsync — defensive self-repair
// against the one-time CoreSync corruption event documented in
// .squad/decisions/inbox/troubleshooter-coresync-vocabprogress-userdeclaredat.md
//
// The bug: every row in VocabularyProgress on at least one Mac Catalyst client
// had literal column NAMES stored as values:
//   UserDeclaredAt    = 'UserDeclaredAt'
//   VerificationState = 'VerificationState'
//   IsUserDeclared    = 'IsUserDeclared'
// EF then threw FormatException on every read; CoreSync forwarded the strings
// to Postgres which rejected them with 42804.
//
// "If a bug came back, it needs a test." (AGENTS.md)

using CoreSync;
using CoreSync.Http.Client;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public class RepairTaintedVocabularyProgressTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SyncService _sut;

    public RepairTaintedVocabularyProgressTests()
    {
        // Single shared in-memory connection so the table survives across calls.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Build SyncService with mocks. The repair method only touches _logger and
        // the DbConnection passed in — the sync providers are never invoked.
        var localSync = new Mock<ISyncProvider>().Object;
        var remoteSync = new Mock<ISyncProviderHttpClient>().Object;
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        _sut = new SyncService(localSync, remoteSync, NullLogger<SyncService>.Instance, serviceProvider);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>
    /// Creates a minimal VocabularyProgress table mirroring the columns the repair
    /// method writes to. We don't need the full schema — just the three repaired
    /// columns plus an Id PK so we can address rows individually.
    /// </summary>
    private void CreateMinimalSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE ""VocabularyProgress"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""UserDeclaredAt"" TEXT NULL,
                ""VerificationState"" INTEGER NOT NULL,
                ""IsUserDeclared"" INTEGER NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    private void InsertRow(string id, object userDeclaredAt, object verificationState, object isUserDeclared)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ""VocabularyProgress"" (""Id"", ""UserDeclaredAt"", ""VerificationState"", ""IsUserDeclared"")
            VALUES ($id, $uda, $vs, $iud)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$uda", userDeclaredAt);
        cmd.Parameters.AddWithValue("$vs", verificationState);
        cmd.Parameters.AddWithValue("$iud", isUserDeclared);
        cmd.ExecuteNonQuery();
    }

    private (string? userDeclaredAt, string verificationState, string isUserDeclared) ReadRow(string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ""UserDeclaredAt"", ""VerificationState"", ""IsUserDeclared""
            FROM ""VocabularyProgress"" WHERE ""Id"" = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (
            reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString(),
            reader.GetValue(1)?.ToString() ?? string.Empty,
            reader.GetValue(2)?.ToString() ?? string.Empty);
    }

    private long CountRowsMatching(string sqlPredicate)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"VocabularyProgress\" WHERE {sqlPredicate}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1: Missing table (fresh DB, pre-MigrateAsync) — must NOT throw.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Repair_WhenTableDoesNotExist_DoesNotThrow()
    {
        // No CreateMinimalSchema() — fresh DB.
        var act = async () => await _sut.RepairTaintedVocabularyProgressAsync(_connection);
        await act.Should().NotThrowAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2: Clean DB — no UPDATEs fire, no rows changed.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Repair_WithCleanData_LeavesRowsUntouched()
    {
        CreateMinimalSchema();
        // Three clean rows: NULL UserDeclaredAt, VerificationState=0, IsUserDeclared=0.
        InsertRow("clean-1", DBNull.Value, 0, 0);
        InsertRow("clean-2", "2026-04-01 12:00:00", 1, 1);
        InsertRow("clean-3", DBNull.Value, 2, 0);

        await _sut.RepairTaintedVocabularyProgressAsync(_connection);

        // Verify nothing changed.
        var (uda1, vs1, iud1) = ReadRow("clean-1");
        uda1.Should().BeNull();
        vs1.Should().Be("0");
        iud1.Should().Be("0");

        var (uda2, vs2, iud2) = ReadRow("clean-2");
        uda2.Should().Be("2026-04-01 12:00:00");
        vs2.Should().Be("1");
        iud2.Should().Be("1");

        var (uda3, vs3, iud3) = ReadRow("clean-3");
        uda3.Should().BeNull();
        vs3.Should().Be("2");
        iud3.Should().Be("0");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3: Fully tainted DB (Captain's exact scenario, 1745-row analogue) —
    //         all three columns repaired, all rows clean afterward.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Repair_WithFullyTaintedData_ResetsAllThreeColumnsToSafeDefaults()
    {
        CreateMinimalSchema();
        // Insert 1745 rows, all tainted, mirroring Captain's machine.
        for (int i = 0; i < 1745; i++)
        {
            InsertRow($"tainted-{i}", "UserDeclaredAt", "VerificationState", "IsUserDeclared");
        }

        await _sut.RepairTaintedVocabularyProgressAsync(_connection);

        // Every row should now match the safe defaults.
        CountRowsMatching("\"UserDeclaredAt\" IS NULL").Should().Be(1745);
        CountRowsMatching("\"VerificationState\" = 0").Should().Be(1745);
        CountRowsMatching("\"IsUserDeclared\" = 0").Should().Be(1745);

        // And no row still carries the tainted literal values.
        CountRowsMatching("\"UserDeclaredAt\" = 'UserDeclaredAt'").Should().Be(0);
        CountRowsMatching("\"VerificationState\" = 'VerificationState'").Should().Be(0);
        CountRowsMatching("\"IsUserDeclared\" = 'IsUserDeclared'").Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4: Partial taint — only the affected columns are touched, clean
    //         columns on the same row are left alone.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Repair_WithPartialTaint_OnlyAffectedColumnsAreReset()
    {
        CreateMinimalSchema();
        // Row tainted only on UserDeclaredAt; other cols clean and meaningful.
        InsertRow("partial-uda", "UserDeclaredAt", 2, 1);
        // Row tainted only on VerificationState.
        InsertRow("partial-vs", "2026-03-15 10:00:00", "VerificationState", 1);
        // Row tainted only on IsUserDeclared.
        InsertRow("partial-iud", "2026-03-15 10:00:00", 2, "IsUserDeclared");

        await _sut.RepairTaintedVocabularyProgressAsync(_connection);

        // partial-uda: UDA repaired to NULL, VerificationState=2 and IsUserDeclared=1 preserved.
        var (uda, vs, iud) = ReadRow("partial-uda");
        uda.Should().BeNull();
        vs.Should().Be("2");
        iud.Should().Be("1");

        // partial-vs: VerificationState repaired to 0, others preserved.
        var (uda2, vs2, iud2) = ReadRow("partial-vs");
        uda2.Should().Be("2026-03-15 10:00:00");
        vs2.Should().Be("0");
        iud2.Should().Be("1");

        // partial-iud: IsUserDeclared repaired to 0, others preserved.
        var (uda3, vs3, iud3) = ReadRow("partial-iud");
        uda3.Should().Be("2026-03-15 10:00:00");
        vs3.Should().Be("2");
        iud3.Should().Be("0");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 5: Idempotency — second run finds nothing to repair, no rows changed.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Repair_RunTwice_SecondRunIsNoOp()
    {
        CreateMinimalSchema();
        for (int i = 0; i < 50; i++)
        {
            InsertRow($"tainted-{i}", "UserDeclaredAt", "VerificationState", "IsUserDeclared");
        }

        // First run: repairs all 50.
        await _sut.RepairTaintedVocabularyProgressAsync(_connection);
        CountRowsMatching("\"UserDeclaredAt\" = 'UserDeclaredAt'").Should().Be(0);

        // Capture state after first run for diff.
        var snapshotBefore = ReadRow("tainted-0");

        // Second run: no-op.
        await _sut.RepairTaintedVocabularyProgressAsync(_connection);

        var snapshotAfter = ReadRow("tainted-0");
        snapshotAfter.Should().BeEquivalentTo(snapshotBefore);

        // Still clean.
        CountRowsMatching("\"UserDeclaredAt\" IS NULL").Should().Be(50);
        CountRowsMatching("\"VerificationState\" = 0").Should().Be(50);
        CountRowsMatching("\"IsUserDeclared\" = 0").Should().Be(50);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 6: EF round-trip — after repair, EF can materialize VocabularyProgress
    //         rows without throwing FormatException, and the cleaned values
    //         round-trip through SaveChanges/Reload with correct CLR types.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Repair_AfterRepair_EFCanMaterializeAndRoundTripVocabularyProgress()
    {
        // Use the real ApplicationDbContext to validate end-to-end EF behavior.
        var efConnection = new SqliteConnection("DataSource=:memory:");
        efConnection.Open();
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlite(efConnection));
            using var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            // Disable FK enforcement for this raw INSERT — we don't care about the
            // VocabularyWord/User parents for the materialization smoke test, and
            // EnsureCreated turns FK enforcement on. The repair method itself doesn't
            // touch FKs, so this only relaxes the test fixture.
            using (var fkCmd = efConnection.CreateCommand())
            {
                fkCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                await fkCmd.ExecuteNonQueryAsync();
            }

            // Inject a tainted row directly via raw SQL (mirrors the corruption event).
            var taintedId = Guid.NewGuid().ToString();
            using (var insertCmd = efConnection.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO ""VocabularyProgress""
                        (""Id"", ""VocabularyWordId"", ""UserId"",
                         ""MasteryScore"", ""TotalAttempts"", ""CorrectAttempts"",
                         ""CurrentStreak"", ""ProductionInStreak"",
                         ""QuizRecognitionDemonstrations"", ""QuizProductionDemonstrations"",
                         ""RecognitionAttempts"", ""RecognitionCorrect"",
                         ""ProductionAttempts"", ""ProductionCorrect"",
                         ""ApplicationAttempts"", ""ApplicationCorrect"",
                         ""CurrentPhase"", ""ReviewInterval"", ""EaseFactor"",
                         ""MultipleChoiceCorrect"", ""TextEntryCorrect"",
                         ""IsPromoted"", ""IsCompleted"",
                         ""IsUserDeclared"", ""UserDeclaredAt"", ""VerificationState"",
                         ""FirstSeenAt"", ""LastPracticedAt"",
                         ""ExposureCount"",
                         ""CreatedAt"", ""UpdatedAt"")
                    VALUES
                        ($id, 'word-1', 'user-1',
                         0.0, 0, 0,
                         0.0, 0,
                         0, 0,
                         0, 0,
                         0, 0,
                         0, 0,
                         0, 1, 2.5,
                         0, 0,
                         0, 0,
                         'IsUserDeclared', 'UserDeclaredAt', 'VerificationState',
                         '2026-03-15 10:00:00', '2026-03-15 10:00:00',
                         0,
                         '2026-03-15 10:00:00', '2026-03-15 10:00:00');";
                insertCmd.Parameters.AddWithValue("$id", taintedId);
                await insertCmd.ExecuteNonQueryAsync();
            }

            // Sanity check: BEFORE repair, EF would throw FormatException trying to
            // materialize this row (the bug we are defending against).
            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var act = async () => await db.VocabularyProgresses.AsNoTracking().ToListAsync();
                await act.Should().ThrowAsync<FormatException>(
                    "EF can't parse the literal string 'UserDeclaredAt' as a DateTime — this is the user-facing bug");
            }

            // Run repair.
            await _sut.RepairTaintedVocabularyProgressAsync(efConnection);

            // AFTER repair: EF materializes cleanly with safe defaults.
            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var rows = await db.VocabularyProgresses.AsNoTracking().ToListAsync();
                rows.Should().HaveCount(1);
                var repaired = rows[0];
                repaired.UserDeclaredAt.Should().BeNull();
                repaired.VerificationState.Should().Be(VerificationStatus.None);
                repaired.IsUserDeclared.Should().BeFalse();
            }

            // Round-trip: write meaningful values back through EF, reload, verify CLR types.
            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var entity = await db.VocabularyProgresses.SingleAsync(vp => vp.Id == taintedId);
                entity.IsUserDeclared = true;
                entity.UserDeclaredAt = new DateTime(2026, 5, 1, 12, 0, 0);
                entity.VerificationState = VerificationStatus.Pending;
                await db.SaveChangesAsync();
            }

            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var entity = await db.VocabularyProgresses.AsNoTracking().SingleAsync(vp => vp.Id == taintedId);
                entity.IsUserDeclared.Should().BeTrue();
                entity.UserDeclaredAt.Should().Be(new DateTime(2026, 5, 1, 12, 0, 0));
                entity.VerificationState.Should().Be(VerificationStatus.Pending);
            }
        }
        finally
        {
            efConnection.Dispose();
        }
    }
}
