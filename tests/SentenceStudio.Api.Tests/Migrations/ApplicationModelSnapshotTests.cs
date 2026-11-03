using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Migrations;

/// <summary>
/// The PostgreSQL snapshot must describe the Phase 2a tables exactly as the server model does
/// (Captain 2026-11-02, docs/sync-all-tables-plan.md), with the API's Npgsql timestamp setting.
/// EF compares them in memory, so no database is needed.
/// </summary>
public sealed class ApplicationModelSnapshotTests
{
    private static readonly string[] Phase2aTables =
    [
        "MinimalPair", "MinimalPairSession", "MinimalPairAttempt", "StreamHistory",
        "ConversationMemoryState",
    ];

    [Fact]
    public void PostgresSnapshotMatchesTheModelForTheTablesConvertedToTextIds()
    {
        // The API host sets this process-wide (src/SentenceStudio.Api/Program.cs).
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        Assert.Contains("20261103015116_SyncTextIdsPhase2a", db.Database.GetMigrations());

        var snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(
            db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model, designTime: true);
        var model = db.GetService<IDesignTimeModel>().Model;
        var differences = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshot.GetRelationalModel(), model.GetRelationalModel())
            .Where(operation => Phase2aTables.Contains(TableOf(operation)))
            // The legacy timestamp switch changes how every DateTime column reads in this process,
            // on every table alike; it is not part of this migration.
            .Where(operation => !(operation is AlterColumnOperation alter
                && IsTimestamp(alter.ColumnType) && IsTimestamp(alter.OldColumn.ColumnType)))
            .Select(operation => $"{operation.GetType().Name} {TableOf(operation)}.{(operation as ColumnOperation)?.Name} {(operation as AlterColumnOperation)?.OldColumn.ColumnType}->{(operation as ColumnOperation)?.ColumnType}")
            .ToList();
        Assert.Empty(differences);
    }

    private static bool IsTimestamp(string? type) =>
        type is not null && type.StartsWith("timestamp", StringComparison.Ordinal);

    private static string? TableOf(MigrationOperation operation) => operation switch
    {
        ColumnOperation column => column.Table,
        TableOperation table => table.Name,
        CreateIndexOperation index => index.Table,
        DropIndexOperation index => index.Table,
        AddForeignKeyOperation key => key.Table,
        DropForeignKeyOperation key => key.Table,
        AddPrimaryKeyOperation key => key.Table,
        DropPrimaryKeyOperation key => key.Table,
        DropColumnOperation column => column.Table,
        _ => null,
    };
}
