using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using SentenceStudio.Data;
using Xunit;

namespace SentenceStudio.SqliteMigrations.Tests;

public sealed class SqliteSnapshotParityTests
{
    [Fact]
    public void EverySqliteMigrationFileIsDiscoveredByTheMobileAssembly()
    {
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("Data Source=:memory:").Options);
        Assert.Equal(typeof(DbContext), typeof(ApplicationDbContext).BaseType);
        Assert.DoesNotContain(db.Model.GetEntityTypes(), entity =>
            entity.GetTableName()?.StartsWith("AspNet", StringComparison.Ordinal) == true
            || entity.GetTableName() == "RefreshTokens");

        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !Directory.Exists(Path.Combine(root.FullName,
                "src", "SentenceStudio.Shared", "Migrations", "Sqlite")))
            root = root.Parent;
        Assert.NotNull(root);

        var migrationsDirectory = Path.Combine(root.FullName,
            "src", "SentenceStudio.Shared", "Migrations", "Sqlite");
        var files = Directory.EnumerateFiles(migrationsDirectory, "*.cs")
            .Select(Path.GetFileName)
            .Where(name => name is not null
                && name.Length >= 18
                && name[..14].All(char.IsDigit)
                && !name.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Select(name => Path.GetFileNameWithoutExtension(name)!)
            .OrderBy(id => id, StringComparer.Ordinal);

        Assert.Equal(files, db.Database.GetMigrations());
    }

    [Fact]
    public void MobileSqliteSnapshotMatchesRuntimeModel()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot
            ?? throw new InvalidOperationException("SQLite model snapshot is missing.");
        var differ = db.GetService<IMigrationsModelDiffer>();
        var snapshotModel = db.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot.Model, designTime: true);
        var changes = differ.GetDifferences(
            snapshotModel.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(changes.Count == 0,
            $"SQLite snapshot differs from the mobile model ({changes.Count} operations):{Environment.NewLine}"
            + string.Join(Environment.NewLine, changes.Select(Describe)));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static string Describe(MigrationOperation operation) => operation switch
    {
        CreateTableOperation change => $"CreateTable {change.Name}",
        DropTableOperation change => $"DropTable {change.Name}",
        AddColumnOperation change => $"AddColumn {change.Table}.{change.Name} {change.ColumnType}",
        DropColumnOperation change => $"DropColumn {change.Table}.{change.Name}",
        AlterColumnOperation change =>
            $"AlterColumn {change.Table}.{change.Name} {change.OldColumn.ColumnType} -> {change.ColumnType}",
        CreateIndexOperation change => $"CreateIndex {change.Table}.{change.Name}",
        DropIndexOperation change => $"DropIndex {change.Table}.{change.Name}",
        _ => operation.GetType().Name
    };
}
