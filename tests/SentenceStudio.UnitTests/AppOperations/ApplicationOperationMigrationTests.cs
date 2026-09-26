using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SentenceStudio.Data;
using PgLedgerMigration = SentenceStudio.Shared.Migrations.AddApplicationOperationLedger;
using SqliteLedgerMigration = SentenceStudio.Shared.Migrations.Sqlite.AddApplicationOperationLedger;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationMigrationTests
{
    private const string MigrationId = "20260903175044_AddApplicationOperationLedger";

    private static readonly string[] LedgerTables =
    [
        "ApplicationOperation",
        "ApplicationOperationConfirmation",
        "ApplicationOperationContinuation",
        "ApplicationOperationEvent",
        "ApplicationOperationReceipt",
        "ApplicationProtectedPayload"
    ];

    private static readonly string[] LedgerIndexes =
    [
        "IX_ApplicationOperation_ParentOperationId",
        "IX_ApplicationOperation_Status_LeaseExpiresAtUtc",
        "IX_ApplicationOperation_UserProfileId_Authority_CapabilityCode_CapabilityVersion_IdempotencyDigest",
        "IX_ApplicationOperation_UserProfileId_PurgeAfterUtc",
        "IX_ApplicationOperation_UserProfileId_Status_ExpiresAtUtc",
        "IX_ApplicationOperationConfirmation_OperationId_ConfirmationDigest",
        "IX_ApplicationOperationConfirmation_UserProfileId_ExpiresAtUtc",
        "IX_ApplicationOperationContinuation_OperationId",
        "IX_ApplicationOperationContinuation_ParentContinuationId",
        "IX_ApplicationOperationContinuation_UserProfileId_ExpiresAtUtc",
        "IX_ApplicationOperationContinuation_UserProfileId_InteractionScopeDigest",
        "IX_ApplicationOperationEvent_OperationId_Sequence",
        "IX_ApplicationOperationEvent_UserProfileId_OccurredAtUtc",
        "IX_ApplicationOperationReceipt_OperationId",
        "IX_ApplicationOperationReceipt_ReversalOperationId",
        "IX_ApplicationOperationReceipt_UserProfileId_CommittedAtUtc",
        "IX_ApplicationProtectedPayload_OperationId",
        "IX_ApplicationProtectedPayload_SubjectKind_SubjectId_ContentKind",
        "IX_ApplicationProtectedPayload_UserProfileId_PurgeAfterUtc"
    ];

    [Fact]
    public void BothProviderMigrationsCarryExactDiscoveryAttributes()
    {
        foreach (var type in new[] { typeof(PgLedgerMigration), typeof(SqliteLedgerMigration) })
        {
            type.GetCustomAttribute<MigrationAttribute>()!.Id.Should().Be(MigrationId);
            type.GetCustomAttribute<DbContextAttribute>()!.ContextType
                .Should().Be(typeof(ApplicationDbContext));
        }
    }

    [Fact]
    public void BothProviderMigrationAssembliesDiscoverTheLedgerMigration()
    {
        using var pg = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=discovery_only;Username=discovery")
                .Options);
        var pgMigrations = pg.GetService<IMigrationsAssembly>().Migrations.Keys;
        pgMigrations.Should().Contain(MigrationId);

        using var sqlite = NewMigrationContext("Data Source=:memory:");
        var sqliteMigrations = sqlite.GetService<IMigrationsAssembly>().Migrations.Keys;
        sqliteMigrations.Should().Contain(MigrationId);
    }

    [Fact]
    public void ProviderMigrationOperationsHaveSchemaParity()
    {
        var pg = new PgLedgerMigration();
        var sqlite = new SqliteLedgerMigration();

        Shape(pg.UpOperations).Should().BeEquivalentTo(
            Shape(sqlite.UpOperations),
            options => options.WithStrictOrdering());
        pg.DownOperations.OfType<DropTableOperation>().Select(operation => operation.Name)
            .Should().BeEquivalentTo(
                sqlite.DownOperations.OfType<DropTableOperation>().Select(operation => operation.Name));
    }

    [Fact]
    public async Task SqliteMigration_UpCreatesHistoryTablesAndIndexes_DownRemovesOnlyLedgerFromBackup()
    {
        var testDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "AppOperationsData",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var sourcePath = Path.Combine(testDirectory, "ledger-up.db");
        var backupPath = Path.Combine(testDirectory, "ledger-down-copy.db");

        try
        {
            await using (var before = new SqliteConnection($"Data Source={sourcePath}"))
            {
                await before.OpenAsync();
                await using var sentinel = before.CreateCommand();
                sentinel.CommandText = "CREATE TABLE Sentinel (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);"
                    + "INSERT INTO Sentinel (Value) VALUES ('preserved');";
                await sentinel.ExecuteNonQueryAsync();
            }

            await using (var db = NewMigrationContext($"Data Source={sourcePath}"))
            {
                await db.Database.MigrateAsync(MigrationId);
            }

            await using (var applied = new SqliteConnection($"Data Source={sourcePath}"))
            {
                await applied.OpenAsync();
                var tables = await NamesAsync(
                    applied,
                    "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;");
                tables.Should().Contain(LedgerTables);
                tables.Should().Contain("Sentinel");

                var indexes = await NamesAsync(
                    applied,
                    "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
                indexes.Should().Contain(LedgerIndexes);

                var history = await NamesAsync(
                    applied,
                    "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;");
                history.Should().Equal(MigrationId);
            }

            await using (var source = new SqliteConnection($"Data Source={sourcePath}"))
            await using (var backup = new SqliteConnection($"Data Source={backupPath}"))
            {
                await source.OpenAsync();
                await backup.OpenAsync();
                source.BackupDatabase(backup);
            }

            await using (var copy = NewMigrationContext($"Data Source={backupPath}"))
            {
                await copy.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
            }

            await using (var reverted = new SqliteConnection($"Data Source={backupPath}"))
            {
                await reverted.OpenAsync();
                var tables = await NamesAsync(
                    reverted,
                    "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;");
                tables.Should().Contain("Sentinel");
                tables.Should().NotContain(LedgerTables);
                var sentinelValue = await ScalarAsync(
                    reverted,
                    "SELECT Value FROM Sentinel WHERE Id = 1;");
                sentinelValue.Should().Be("preserved");
                var history = await NamesAsync(
                    reverted,
                    "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;");
                history.Should().BeEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static ApplicationDbContext NewMigrationContext(string connectionString) =>
        new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(
                    connectionString,
                    options => options.MigrationsAssembly(
                        typeof(SqliteLedgerMigration).Assembly.GetName().Name))
                .Options);

    private static IReadOnlyList<object> Shape(IReadOnlyList<MigrationOperation> operations)
    {
        var tables = operations.OfType<CreateTableOperation>()
            .Select(operation => new
            {
                Kind = "table",
                operation.Name,
                Columns = operation.Columns.Select(column => new
                {
                    column.Name,
                    ClrType = column.ClrType.FullName,
                    column.IsNullable,
                    column.MaxLength
                }).ToArray(),
                Checks = operation.CheckConstraints
                    .Select(check => new { check.Name, check.Sql })
                    .OrderBy(check => check.Name, StringComparer.Ordinal)
                    .ToArray(),
                ForeignKeys = operation.ForeignKeys.Select(key => new
                {
                    Columns = key.Columns.ToArray(),
                    key.PrincipalTable,
                    PrincipalColumns = key.PrincipalColumns.ToArray(),
                    key.OnDelete
                }).ToArray()
            });
        var indexes = operations.OfType<CreateIndexOperation>()
            .Select(operation => new
            {
                Kind = "index",
                operation.Table,
                Columns = operation.Columns.ToArray(),
                operation.IsUnique,
                operation.Filter
            });

        return tables.Cast<object>().Concat(indexes).ToArray();
    }

    private static async Task<string[]> NamesAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }
}
