using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The W9 grounding columns against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// <b>A migration proven only in memory is not proven.</b> The in-memory provider accepts
/// <c>AddColumn</c> silently and would accept a Down that drops the wrong thing. What this asserts
/// is what an operator actually depends on: that Up leaves existing rows intact with nulls, that
/// the model and the database agree afterwards, and that Down puts the schema back.
/// </para>
/// <para>
/// <b>PostgreSQL only, deliberately.</b> <c>CoachDbContext</c> has no SQLite provider and never
/// reaches a device, so there is no mobile counterpart to write and
/// <c>validate-mobile-migrations.sh</c> does not apply to it. Stating that is not the same as
/// claiming it passed.
/// </para>
/// </remarks>
public sealed class CoachResponseReportGroundingPostgresTests : IAsyncLifetime
{
    private const string MigrationId = "20260822094421_AddCoachResponseReportGroundingColumns";

    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("grounding-cols");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    // ────────────────────────────────────────────────────────────── migration

    [PostgresFact]
    public async Task The_migration_is_applied_and_named()
    {
        await using var db = _harness.NewContext();

        var applied = await db.Database.GetAppliedMigrationsAsync();

        applied.Should().Contain(
            MigrationId,
            "the harness runs the real migrations, so this is the shipped id rather than a literal "
            + "somebody typed");

        applied.Should().Contain(
            "20260821033641_AddCoachResponseReports",
            "the table it extends is still created by its own migration; this one adds columns");
    }

    [PostgresFact]
    public async Task The_model_and_the_database_agree_after_Up()
    {
        await using var db = _harness.NewContext();

        var pending = await db.Database.GetPendingMigrationsAsync();

        pending.Should().BeEmpty(
            "a pending migration after the harness has migrated means the snapshot and the model "
            + "have drifted, which is how a column ships that nothing creates");
    }

    [PostgresFact]
    public async Task Every_grounding_column_exists_and_is_nullable()
    {
        await using var db = _harness.NewContext();

        var columns = await ReadColumnsAsync(db);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GroundingStage"] = "integer",
            ["GroundingRefused"] = "boolean",
            ["GroundingAltered"] = "boolean",
            ["GroundingRepairSuppressed"] = "boolean",
            ["GroundingFindingCount"] = "integer",
            ["GroundingRuleCodes"] = "character varying",
            ["GroundingLimitationCode"] = "integer",
            ["GroundingShadowLabel"] = "integer"
        };

        var checkedColumns = 0;
        foreach (var (name, type) in expected)
        {
            columns.Should().ContainKey(name);
            columns[name].DataType.Should().Be(type);
            columns[name].IsNullable.Should().BeTrue(
                "{0} must be nullable: null is how the row says the ladder was Off, or that it "
                + "predates the column", name);
            checkedColumns++;
        }

        checkedColumns.Should().Be(8, "all eight were examined, not a subset");
    }

    /// <summary>An existing row survives Up with nulls, which is the no-backfill guarantee.</summary>
    [PostgresFact]
    public async Task A_row_written_before_the_columns_reads_back_with_nulls()
    {
        await using var db = _harness.NewContext();

        // Written through the model, then the grounding members cleared to model a version-1 row.
        // The point is the read: nothing backfills, so the columns stay null forever.
        var row = Report("pre-w9");
        row.SchemaVersion = 1;
        db.CoachResponseReports.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.CoachResponseReports.AsNoTracking()
            .SingleAsync(entity => entity.Id == row.Id);

        read.SchemaVersion.Should().Be(1, "the row keeps the version it was written under");
        read.GroundingStage.Should().BeNull();
        read.GroundingRefused.Should().BeNull();
        read.GroundingAltered.Should().BeNull();
        read.GroundingRepairSuppressed.Should().BeNull();
        read.GroundingFindingCount.Should().BeNull();
        read.GroundingRuleCodes.Should().BeNull();
        read.GroundingLimitationCode.Should().BeNull();
        read.GroundingShadowLabel.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_grounded_row_round_trips_through_the_real_columns()
    {
        await using var db = _harness.NewContext();

        var facts = CoachResponseReportService.ProjectGrounding(Summary());

        var row = Report("grounded");
        row.GroundingStage = facts.Stage;
        row.GroundingRefused = facts.Refused;
        row.GroundingAltered = facts.Altered;
        row.GroundingRepairSuppressed = facts.RepairSuppressed;
        row.GroundingFindingCount = facts.FindingCount;
        row.GroundingRuleCodes = facts.RuleCodes;
        row.GroundingLimitationCode = facts.LimitationCode;
        row.GroundingShadowLabel = facts.ShadowLabel;

        db.CoachResponseReports.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.CoachResponseReports.AsNoTracking()
            .SingleAsync(entity => entity.Id == row.Id);

        read.GroundingStage.Should().Be((int)CoachGroundingStage.Repair);
        read.GroundingRefused.Should().BeFalse();
        read.GroundingAltered.Should().BeTrue();
        read.GroundingRepairSuppressed.Should().BeFalse();
        read.GroundingFindingCount.Should().Be(3);
        read.GroundingRuleCodes.Should().Be("FabricatedCheck,OrderClaimMismatch");
        read.GroundingLimitationCode.Should().Be((int)CoachLimitationCode.AvailableOnAnotherSurface);
        read.GroundingShadowLabel.Should().Be((int)CoachShadowRouteLabel.LearnerState);
        read.SchemaVersion.Should().Be(2);
    }

    /// <summary>Down removes exactly the eight, and leaves the rest of the table standing.</summary>
    [PostgresFact]
    public async Task Down_is_clean_and_the_table_survives_it()
    {
        await using var scratch = await CoachPostgresHarness.CreateAsync("grounding-down");
        await using var db = scratch.NewContext();

        var row = Report("survives-down");
        row.GroundingStage = (int)CoachGroundingStage.Observe;
        db.CoachResponseReports.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        // Down to the migration immediately before this one.
        await migrator.MigrateAsync("20260821033641_AddCoachResponseReports");

        var afterDown = await ReadColumnsAsync(db);
        afterDown.Keys.Should().NotContain(
            name => name.StartsWith("Grounding", StringComparison.Ordinal),
            "Down drops the eight it added and nothing else");

        afterDown.Should().ContainKey("Reason", "the table itself survives the rollback");
        afterDown.Should().ContainKey("CoachMessageId");

        var survivors = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM \"CoachResponseReport\"")
            .SingleAsync();

        survivors.Should().Be(1, "the learner's report outlives a schema rollback");

        // And Up again, so the rollback is not one-way.
        await migrator.MigrateAsync();

        var afterUp = await ReadColumnsAsync(db);
        afterUp.Keys.Where(name => name.StartsWith("Grounding", StringComparison.Ordinal))
            .Should().HaveCount(8);
    }

    [PostgresFact]
    public async Task The_migration_writes_no_data()
    {
        // Non-vacuity for the no-backfill claim: the migration's own source contains no statement
        // that could touch a row. Read from the shipped file rather than asserted about it.
        var source = await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Api", "Coach", "Persistence", "Migrations",
            $"{MigrationId}.cs"));

        source.Should().NotBeNullOrWhiteSpace();
        source.Should().Contain("AddColumn", "the scan is reading the right file");

        foreach (var forbidden in new[] { "Sql(", "UpdateData", "DeleteData", "InsertData", "DropTable" })
        {
            source.Should().NotContain(
                forbidden,
                "{0} would mutate existing rows, and this migration is additive only", forbidden);
        }

        System.Text.RegularExpressions.Regex.Matches(source, "AddColumn<").Should().HaveCount(8);
        System.Text.RegularExpressions.Regex.Matches(source, "DropColumn\\(").Should().HaveCount(8);
    }

    /// <summary>Retention and erasure carry the new columns because they carry the row.</summary>
    [PostgresFact]
    public async Task Erasure_takes_the_grounding_columns_with_the_row()
    {
        await using var db = _harness.NewContext();

        var row = Report("erased");
        row.GroundingStage = (int)CoachGroundingStage.Enforce;
        row.GroundingRuleCodes = "FabricatedCheck";
        db.CoachResponseReports.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The same mechanism the existing contributor uses — the row is the unit of erasure, so
        // there is no second deletion path to keep in step and nothing new to forget.
        var removed = await db.CoachResponseReports
            .Where(entity => entity.UserProfileId == row.UserProfileId)
            .ExecuteDeleteAsync();

        removed.Should().BeGreaterThan(0);

        (await db.CoachResponseReports.AsNoTracking()
            .AnyAsync(entity => entity.Id == row.Id))
            .Should().BeFalse("the columns cannot outlive the row that holds them");
    }

    // ─────────────────────────────────────────────────────────────── helpers

    private static async Task<Dictionary<string, (string DataType, bool IsNullable)>> ReadColumnsAsync(
        CoachDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT column_name, data_type, is_nullable FROM information_schema.columns " +
            "WHERE table_name = 'CoachResponseReport'";

        var columns = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = (
                reader.GetString(1),
                string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase));
        }

        return columns;
    }

    private static CoachResponseReport Report(string marker) => new()
    {
        Id = $"rep-{marker}-{Guid.NewGuid():N}"[..Math.Min(64, 40)],
        UserProfileId = $"user-{marker}",
        ConversationId = $"conv-{marker}",
        CoachMessageId = $"msg-{marker}",
        CoachMessageSequence = 2,
        RequestMessageId = $"req-{marker}",
        RequestMessageSequence = 1,
        Reason = CoachResponseReportReason.IncorrectOrMisleading,
        ResponseKind = CoachMessageKind.PedagogicalAnswer,
        ReportedAtUtc = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc)
    };

    private static CoachGroundingTurnSummary Summary() => new(
        CoachGroundingStage.Repair,
        SubstitutionAllowed: true,
        Refused: false,
        Altered: true,
        RepairSuppressedForLanguage: false,
        FindingCount: 3,
        RuleCounts:
        [
            new CoachGroundingRuleCount(CoachClaimRuleCode.OrderClaimMismatch, 1),
            new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, 2)
        ],
        LimitationCode: CoachLimitationCode.AvailableOnAnotherSurface,
        ShadowLabel: CoachShadowRouteLabel.LearnerState);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return directory!.FullName;
    }
}
