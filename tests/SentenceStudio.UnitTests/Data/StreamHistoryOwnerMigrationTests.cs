using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using PgMigration = SentenceStudio.Shared.Migrations.AddStreamHistoryOwnerScope;
using SqliteMigration = SentenceStudio.Shared.Migrations.Sqlite.AddStreamHistoryOwnerScope;

namespace SentenceStudio.UnitTests.Data;

public sealed class StreamHistoryOwnerMigrationTests
{
    private const string MigrationId = "20260927100455_AddStreamHistoryOwnerScope";
    private const string IndexName = "IX_StreamHistory_UserProfileId_CreatedAt";

    [Fact]
    public void BothProvidersDiscoverTheSameMigrationId()
    {
        foreach (var migrationType in new[] { typeof(PgMigration), typeof(SqliteMigration) })
        {
            migrationType.GetCustomAttribute<DbContextAttribute>()!.ContextType
                .Should().Be(typeof(ApplicationDbContext));
            migrationType.GetCustomAttribute<MigrationAttribute>()!.Id.Should().Be(MigrationId);
        }

        using var postgres = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=discovery_only;Username=discovery")
                .Options);
        postgres.GetService<IMigrationsAssembly>().Migrations.Keys.Should().Contain(MigrationId);

        using var sqlite = MigrationContext("Data Source=:memory:");
        sqlite.GetService<IMigrationsAssembly>().Migrations.Keys.Should().Contain(MigrationId);
    }

    [Fact]
    public void BothUpOperationsAddOnlyNullableOwnerAndIndex_WithoutClaimingLegacyData()
    {
        foreach (var (migration, type) in new (Migration, string)[]
        {
            (new PgMigration(), "text"),
            (new SqliteMigration(), "TEXT")
        })
        {
            var addColumn = migration.UpOperations.OfType<AddColumnOperation>()
                .Should().ContainSingle().Which;
            addColumn.Table.Should().Be("StreamHistory");
            addColumn.Name.Should().Be(nameof(StreamHistory.UserProfileId));
            addColumn.ColumnType.Should().Be(type);
            addColumn.IsNullable.Should().BeTrue();

            var index = migration.UpOperations.OfType<CreateIndexOperation>()
                .Should().ContainSingle().Which;
            index.Table.Should().Be("StreamHistory");
            index.Name.Should().Be(IndexName);
            index.Columns.Should().Equal("UserProfileId", "CreatedAt");
            migration.UpOperations.Should().HaveCount(2);
            migration.UpOperations.Should().NotContain(op =>
                typeof(SqlOperation).IsInstanceOfType(op)
                || typeof(DeleteDataOperation).IsInstanceOfType(op)
                || typeof(DropTableOperation).IsInstanceOfType(op)
                || typeof(AlterColumnOperation).IsInstanceOfType(op));

            migration.DownOperations.OfType<DropIndexOperation>()
                .Should().ContainSingle().Which.Name.Should().Be(IndexName);
            migration.DownOperations.OfType<DropColumnOperation>()
                .Should().ContainSingle().Which.Name.Should().Be(nameof(StreamHistory.UserProfileId));
        }
    }

    [Theory]
    [InlineData("Migrations", "text")]
    [InlineData("Migrations/Sqlite", "TEXT")]
    public void ProviderSnapshotContainsOwnerMappingAndOwnerFirstIndex(
        string migrationDirectory, string columnType)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !Directory.Exists(Path.Combine(root.FullName, "src", "SentenceStudio.Shared")))
            root = root.Parent;
        root.Should().NotBeNull();

        var snapshot = File.ReadAllText(Path.Combine(
            root!.FullName, "src", "SentenceStudio.Shared",
            migrationDirectory.Replace('/', Path.DirectorySeparatorChar),
            "ApplicationDbContextModelSnapshot.cs"));
        var start = snapshot.IndexOf(
            "modelBuilder.Entity(\"SentenceStudio.Shared.Models.StreamHistory\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var end = snapshot.IndexOf("modelBuilder.Entity(", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? snapshot[start..] : snapshot[start..end];

        var ownerPropertyStart = section.IndexOf(
            "b.Property<string>(\"UserProfileId\")", StringComparison.Ordinal);
        ownerPropertyStart.Should().BeGreaterThan(-1);
        var nextProperty = section.IndexOf(
            "b.Property<", ownerPropertyStart + 1, StringComparison.Ordinal);
        var ownerProperty = nextProperty < 0
            ? section[ownerPropertyStart..]
            : section[ownerPropertyStart..nextProperty];
        ownerProperty.Should().Contain($"HasColumnType(\"{columnType}\")");
        section.Should().Contain("b.HasIndex(\"UserProfileId\", \"CreatedAt\")");
    }

    [Fact]
    public async Task SqliteEfMigration_PreservesLegacyRowWithNullOwnerAndRecordsDiscovery()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var previousCreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        int previousId;
        await using (var legacy = new LegacyHistoryDbContext(
            new DbContextOptionsBuilder<LegacyHistoryDbContext>().UseSqlite(connection).Options))
        {
            await legacy.Database.EnsureCreatedAsync();
            var original = new StreamHistory
            {
                Phrase = "original recording",
                AudioFilePath = "preserved-audio.mp3",
                CreatedAt = previousCreatedAt,
                UpdatedAt = previousCreatedAt
            };
            legacy.StreamHistories.Add(original);
            await legacy.SaveChangesAsync();
            previousId = original.Id;
        }

        await using var db = MigrationContext(connection);
        await db.Database.MigrateAsync(MigrationId);

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(MigrationId);
        var previous = await db.StreamHistories.AsNoTracking().SingleAsync();
        previous.Id.Should().Be(previousId);
        previous.Phrase.Should().Be("original recording");
        previous.AudioFilePath.Should().Be("preserved-audio.mp3");
        previous.CreatedAt.Should().Be(previousCreatedAt);
        previous.UserProfileId.Should().BeNull();

        await using var column = connection.CreateCommand();
        column.CommandText = """
            SELECT name FROM pragma_table_info('StreamHistory')
            WHERE name = 'UserProfileId' AND type = 'TEXT' AND "notnull" = 0;
            """;
        (await column.ExecuteScalarAsync() as string).Should().Be("UserProfileId");

        await using var index = connection.CreateCommand();
        index.CommandText = """
            SELECT name FROM sqlite_master WHERE type = 'index'
            AND tbl_name = 'StreamHistory' AND name = 'IX_StreamHistory_UserProfileId_CreatedAt';
            """;
        (await index.ExecuteScalarAsync() as string).Should().Be(IndexName);
    }

    private static ApplicationDbContext MigrationContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString, options => options.MigrationsAssembly(
                typeof(SqliteMigration).Assembly.GetName().Name))
            .Options);

    private static ApplicationDbContext MigrationContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly(
                typeof(SqliteMigration).Assembly.GetName().Name))
            .Options);

    private sealed class LegacyHistoryDbContext(DbContextOptions<LegacyHistoryDbContext> options)
        : DbContext(options)
    {
        public DbSet<StreamHistory> StreamHistories => Set<StreamHistory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StreamHistory>().ToTable("StreamHistory").HasKey(h => h.Id);
            modelBuilder.Entity<StreamHistory>().Ignore(h => h.UserProfileId);
        }
    }
}
