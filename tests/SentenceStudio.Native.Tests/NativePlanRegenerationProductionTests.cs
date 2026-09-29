using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using PostgresMigration = SentenceStudio.Shared.Migrations.ArchiveDisplacedPlanItems;
using SqliteMigration = SentenceStudio.Shared.Migrations.Sqlite.ArchiveDisplacedPlanItems;

namespace SentenceStudio.Native.Tests;

public sealed class NativePlanRegenerationProductionTests
{
    private const string MigrationId = "20260928050905_ArchiveDisplacedPlanItems";
    private const string ActiveIndex = "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId";
    private static readonly DateOnly LocalDate = new(2026, 9, 28);
    private static readonly DateTime DateKey =
        LocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    [Fact]
    public void BothProviderMigrationsAreDiscoverableAndFilterOnlyActiveItems()
    {
        foreach (var (migration, filter, columnType) in new (Migration, string, string)[]
        {
            (new PostgresMigration(), "\"IsArchived\" = FALSE", "boolean"),
            (new SqliteMigration(), "\"IsArchived\" = 0", "INTEGER")
        })
        {
            var type = migration.GetType();
            Assert.Equal(typeof(ApplicationDbContext),
                type.GetCustomAttribute<DbContextAttribute>()?.ContextType);
            Assert.Equal(MigrationId, type.GetCustomAttribute<MigrationAttribute>()?.Id);

            var column = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
            Assert.Equal(nameof(DailyPlanCompletion.IsArchived), column.Name);
            Assert.Equal("DailyPlanCompletion", column.Table);
            Assert.Equal(columnType, column.ColumnType);
            Assert.False(column.IsNullable);
            Assert.Equal(false, column.DefaultValue);

            Assert.Equal(ActiveIndex, Assert.Single(
                migration.UpOperations.OfType<DropIndexOperation>()).Name);
            var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
            Assert.Equal(ActiveIndex, index.Name);
            Assert.True(index.IsUnique);
            Assert.Equal(filter, index.Filter);
            Assert.Equal(new[] { "UserProfileId", "Date", "PlanItemId" }, index.Columns);
            if (migration is SqliteMigration)
            {
                var failure = Assert.Throws<NotSupportedException>(() =>
                {
                    _ = migration.DownOperations;
                });
                Assert.Contains("CoreSync", failure.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Single(migration.DownOperations.OfType<SqlOperation>());
            }
        }

        using var postgres = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=discovery_only;Username=discovery")
                .Options);
        Assert.Contains(MigrationId, postgres.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Equal("\"IsArchived\" = FALSE", ActivePlanIndex(postgres).GetFilter());

        using var sqlite = MigrationContext(new SqliteConnection("Data Source=:memory:"));
        Assert.Contains(MigrationId, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Equal("\"IsArchived\" = 0", ActivePlanIndex(sqlite).GetFilter());
    }

    [Theory]
    [InlineData(false, "boolean", "\"IsArchived\" = FALSE")]
    [InlineData(true, "INTEGER", "\"IsArchived\" = 0")]
    public void ProviderSnapshotsContainTheArchiveDefaultAndFilteredIndex(
        bool sqlite, string columnType, string filter)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !Directory.Exists(Path.Combine(root.FullName, "src", "SentenceStudio.Shared")))
            root = root.Parent;
        Assert.NotNull(root);

        var migrationDirectory = Path.Combine(root!.FullName, "src",
            "SentenceStudio.Shared", "Migrations");
        if (sqlite)
            migrationDirectory = Path.Combine(migrationDirectory, "Sqlite");
        var snapshot = File.ReadAllText(Path.Combine(
            migrationDirectory, "ApplicationDbContextModelSnapshot.cs"));
        var start = snapshot.IndexOf(
            "modelBuilder.Entity(\"SentenceStudio.Shared.Models.DailyPlanCompletion\"",
            StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = snapshot.IndexOf("modelBuilder.Entity(", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? snapshot[start..] : snapshot[start..end];
        Assert.Contains($"b.Property<bool>(\"IsArchived\")", section, StringComparison.Ordinal);
        Assert.Contains(".IsConcurrencyToken()", section, StringComparison.Ordinal);
        Assert.Contains($".HasColumnType(\"{columnType}\")", section, StringComparison.Ordinal);
        Assert.Contains(".HasDefaultValue(false)", section, StringComparison.Ordinal);
        Assert.Contains($".HasFilter(\"{filter.Replace("\"", "\\\"")}\")",
            section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteEfUpgradePreservesExistingRow_AndDownFailsClosedOnScratchBackups()
    {
        await using var source = new SqliteConnection("Data Source=:memory:");
        await source.OpenAsync();
        await using (var legacy = LegacyContext(source))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Completions.Add(Row("original", NativeSliceFixture.UserA,
                DateKey, "stable-item", minutes: 7, completed: true));
            await legacy.SaveChangesAsync();
        }

        await using (var command = source.CreateCommand())
        {
            // A provisioned native database has table-bound CoreSync tracking
            // triggers. A harmless stand-in proves rollback never drops them.
            command.CommandText = """
                CREATE TRIGGER "ScratchPlanTrackingTrigger"
                AFTER INSERT ON "DailyPlanCompletion"
                BEGIN SELECT 1; END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = MigrationContext(source))
        {
            Assert.Contains(MigrationId, await db.Database.GetPendingMigrationsAsync());
            await db.Database.MigrateAsync(MigrationId);
            Assert.Contains(MigrationId, await db.Database.GetAppliedMigrationsAsync());

            var original = await db.DailyPlanCompletions.AsNoTracking().SingleAsync();
            Assert.Equal("original", original.Id);
            Assert.Equal("stable-item", original.PlanItemId);
            Assert.Equal(7, original.MinutesSpent);
            Assert.True(original.IsCompleted);
            Assert.False(original.IsArchived);
            Assert.Equal(DateKey.AddHours(10), original.CompletedAt);
        }

        Assert.Equal("IsArchived", await ScalarAsync(source, """
            SELECT name FROM pragma_table_info('DailyPlanCompletion')
            WHERE name = 'IsArchived' AND type = 'INTEGER' AND "notnull" = 1;
            """));
        Assert.Contains("WHERE \"IsArchived\" = 0",
            (await IndexSqlAsync(source))!, StringComparison.Ordinal);
        var trackingTrigger = await TrackingTriggerSqlAsync(source);
        Assert.NotNull(trackingTrigger);

        await using var backup = new SqliteConnection("Data Source=:memory:");
        await backup.OpenAsync();
        source.BackupDatabase(backup);
        var cleanIndex = await IndexSqlAsync(backup);

        await using (var clean = MigrationContext(backup))
        {
            var failure = await Assert.ThrowsAsync<NotSupportedException>(() =>
                clean.Database.MigrateAsync("0"));
            Assert.Contains("CoreSync", failure.Message, StringComparison.Ordinal);
            Assert.Contains(MigrationId, await clean.Database.GetAppliedMigrationsAsync());
        }
        Assert.Equal("IsArchived", await ScalarAsync(backup, """
            SELECT name FROM pragma_table_info('DailyPlanCompletion')
            WHERE name = 'IsArchived';
            """));
        Assert.Equal(cleanIndex, await IndexSqlAsync(backup));
        Assert.Equal(trackingTrigger, await TrackingTriggerSqlAsync(backup));
        await using (var oldSchema = LegacyContext(backup))
        {
            var original = await oldSchema.Completions.AsNoTracking().SingleAsync();
            Assert.Equal("original", original.Id);
            Assert.Equal("stable-item", original.PlanItemId);
            Assert.Equal(7, original.MinutesSpent);
            Assert.True(original.IsCompleted);
        }

        await using (var db = MigrationContext(source))
        {
            var original = await db.DailyPlanCompletions.SingleAsync();
            original.IsArchived = true;
            await db.SaveChangesAsync();
            db.DailyPlanCompletions.Add(Row("replacement", NativeSliceFixture.UserA,
                DateKey, "stable-item", minutes: 0, completed: false));
            db.DailyPlanCompletions.Add(Row("older-archive", NativeSliceFixture.UserA,
                DateKey, "stable-item", minutes: 4, completed: false, archived: true));
            await db.SaveChangesAsync();
        }

        await using (var duplicate = MigrationContext(source))
        {
            duplicate.DailyPlanCompletions.Add(Row("duplicate-active", NativeSliceFixture.UserA,
                DateKey, "stable-item", minutes: 0, completed: false));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        await using var archivedBackup = new SqliteConnection("Data Source=:memory:");
        await archivedBackup.OpenAsync();
        source.BackupDatabase(archivedBackup);
        var archivedIndex = await IndexSqlAsync(archivedBackup);

        await using (var guarded = MigrationContext(archivedBackup))
        {
            var before = await guarded.DailyPlanCompletions.AsNoTracking()
                .OrderBy(row => row.Id).ToListAsync();
            var failure = await Assert.ThrowsAsync<NotSupportedException>(() =>
                guarded.Database.MigrateAsync("0"));
            Assert.Contains("CoreSync", failure.Message, StringComparison.Ordinal);
            Assert.Contains(MigrationId, await guarded.Database.GetAppliedMigrationsAsync());
            var after = await guarded.DailyPlanCompletions.AsNoTracking()
                .OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(3, before.Count);
            Assert.Equal(2, before.Count(row => row.IsArchived));
            Assert.Equal(
                before.Select(row => (row.Id, row.PlanItemId, row.IsArchived,
                    row.IsCompleted, row.MinutesSpent, row.CompletedAt)),
                after.Select(row => (row.Id, row.PlanItemId, row.IsArchived,
                    row.IsCompleted, row.MinutesSpent, row.CompletedAt)));
        }
        Assert.Equal(archivedIndex, await IndexSqlAsync(archivedBackup));
        Assert.Equal(trackingTrigger, await TrackingTriggerSqlAsync(archivedBackup));
    }

    [Fact]
    public async Task RegenerateArchivesCompletedStartedAndPendingRows_WithoutCrossingOwnerOrDate()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resource = await db.LearningResources.SingleAsync(
                row => row.Id == fixture.ResourceId(NativeSliceFixture.UserA));
            resource.Transcript = "Synthetic reading transcript";
            await db.SaveChangesAsync();
        }
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("VocabularyReview", null, null, 10, 1),
            ("Reading", fixture.ResourceId(NativeSliceFixture.UserA), null, 15, 2),
            ("Writing", fixture.ResourceId(NativeSliceFixture.UserA), null, 5, 3));

        var original = await GenerateAsync(fixture, NativeSliceFixture.UserA);
        var completedId = original.Items.Single(i => i.ActivityType == "VocabularyReview").Id;
        var startedId = original.Items.Single(i => i.ActivityType == "Reading").Id;
        var pendingId = original.Items.Single(i => i.ActivityType == "Writing").Id;
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.NotNull(await lease.Service.MarkCompleteAsync(LocalDate, completedId, 8));
            Assert.True(await lease.Service.UpdateProgressAsync(LocalDate, startedId, 4));
        }

        var before = await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.DailyPlanCompletions.Add(Row("previous-day", NativeSliceFixture.UserA,
                DateKey.AddDays(-1), "yesterday-item", minutes: 6, completed: true));
            await db.SaveChangesAsync();
            var progress = scope.ServiceProvider.GetRequiredService<IProgressService>();
            Assert.Equal(3, (await progress.GetCachedPlanAsync(DateKey))!.Items.Count);
        }

        fixture.Activate(NativeSliceFixture.UserB);
        fixture.SetPlanActivities(NativeSliceFixture.UserB,
            ("VocabularyReview", null, null, 10, 1));
        await GenerateAsync(fixture, NativeSliceFixture.UserB);
        var otherOwner = Assert.Single(await RowsAsync(fixture, NativeSliceFixture.UserB, DateKey));
        Assert.Equal(completedId, otherOwner.PlanItemId);

        fixture.Activate(NativeSliceFixture.UserA);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Cloze", fixture.ResourceId(NativeSliceFixture.UserA), null, 6, 1));
        var replacement = Assert.Single((await GenerateAsync(fixture, NativeSliceFixture.UserA)).Items);
        Assert.Equal(0, replacement.MinutesSpent);
        Assert.False(replacement.IsCompleted);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.Equal(replacement.Id, Assert.Single((await lease.Service.GetTodayAsync())!.Items).Id);
            Assert.Equal(replacement.Id, Assert.Single(
                (await lease.Service.GetTodaySnapshotAsync()).Items).PlanItemId);
            foreach (var displacedId in new[] { completedId, startedId, pendingId })
            {
                Assert.False(await lease.Service.UpdateProgressAsync(LocalDate, displacedId, 30));
                Assert.Null(await lease.Service.MarkCompleteAsync(LocalDate, displacedId, 30));
            }
        }
        using (var scope = fixture.Scope())
        {
            var progress = scope.ServiceProvider.GetRequiredService<IProgressService>();
            await progress.MarkPlanItemCompleteAsync(completedId, 30);
            await progress.UpdatePlanItemProgressAsync(pendingId, 30);
            Assert.False(await progress.UpdatePlanItemProgressAsync(
                NativeSliceFixture.UserA, startedId, 30));
            Assert.Null(await progress.ValidatePlanItemAsync(
                NativeSliceFixture.UserA, completedId, PlanActivityType.VocabularyReview,
                resourceId: null, skillId: null,
                vocabularyWordIds: [fixture.WordId(NativeSliceFixture.UserA, 0)]));
        }

        var rows = await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey);
        Assert.Equal(4, rows.Count);
        Assert.Single(rows, row => !row.IsArchived && row.PlanItemId == replacement.Id);
        foreach (var previous in before)
        {
            var archived = Assert.Single(rows, row => row.Id == previous.Id);
            Assert.True(archived.IsArchived);
            Assert.Equal(previous.PlanItemId, archived.PlanItemId);
            Assert.Equal(previous.MinutesSpent, archived.MinutesSpent);
            Assert.Equal(previous.IsCompleted, archived.IsCompleted);
            Assert.Equal(previous.CompletedAt, archived.CompletedAt);
        }
        Assert.Equal(0, rows.Single(row => row.PlanItemId == pendingId).MinutesSpent);
        Assert.Equal(4, rows.Single(row => row.PlanItemId == startedId).MinutesSpent);
        Assert.True(rows.Single(row => row.PlanItemId == completedId).IsCompleted);
        Assert.Equal("previous-day", Assert.Single(
            await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey.AddDays(-1))).Id);
        Assert.False(Assert.Single(
            await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey.AddDays(-1))).IsArchived);
        var unaffected = Assert.Single(await RowsAsync(fixture, NativeSliceFixture.UserB, DateKey));
        Assert.Equal(otherOwner.Id, unaffected.Id);
        Assert.False(unaffected.IsArchived);
        var history = await HistoryAsync(fixture);
        Assert.Equal(4, history.Count);
        Assert.Contains(history, entry => entry.PlanItemId == completedId
            && entry.IsCompleted && entry.MinutesSpent == 8);
        Assert.Contains(history, entry => entry.PlanItemId == startedId
            && !entry.IsCompleted && entry.MinutesSpent == 4);
        Assert.Contains(history, entry => entry.PlanItemId == pendingId
            && !entry.IsCompleted && entry.MinutesSpent == 0);
        using (var scope = fixture.Scope())
        {
            var progress = scope.ServiceProvider.GetRequiredService<IProgressService>();
            var cached = await progress.GetCachedPlanAsync(DateKey);
            Assert.Equal(replacement.Id, Assert.Single(cached!.Items).Id);
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.Equal(replacement.Id, Assert.Single((await lease.Service.GetTodayAsync())!.Items).Id);
        Assert.Equal(4, (await HistoryAsync(fixture)).Count);
    }

    [Fact]
    public async Task ReintroducingAnArchivedItemCreatesAFreshRow_WhileMatchingActiveProgressIsMonotonic()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Reading", null, null, 15, 1));
        var first = Assert.Single((await GenerateAsync(fixture, NativeSliceFixture.UserA)).Items);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.NotNull(await lease.Service.MarkCompleteAsync(LocalDate, first.Id, 9));
        using (var scope = fixture.Scope())
        {
            var progress = scope.ServiceProvider.GetRequiredService<IProgressService>();
            Assert.Equal(9, Assert.Single((await progress.GetCachedPlanAsync(DateKey))!.Items).MinutesSpent);
        }
        var oldRow = Assert.Single(await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey));

        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Writing", fixture.ResourceId(NativeSliceFixture.UserA), null, 5, 1));
        await GenerateAsync(fixture, NativeSliceFixture.UserA);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Reading", null, null, 15, 1));
        var returned = Assert.Single((await GenerateAsync(fixture, NativeSliceFixture.UserA)).Items);
        Assert.Equal(first.Id, returned.Id);
        Assert.False(returned.IsCompleted);
        Assert.Equal(0, returned.MinutesSpent);
        var rows = await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey);
        var archived = Assert.Single(rows, row => row.Id == oldRow.Id);
        var active = Assert.Single(rows, row => row.PlanItemId == first.Id && !row.IsArchived);
        Assert.True(archived.IsArchived);
        Assert.Equal(9, archived.MinutesSpent);
        Assert.Equal(oldRow.CompletedAt, archived.CompletedAt);
        Assert.NotEqual(oldRow.Id, active.Id);

        using (var scope = fixture.Scope())
        {
            var progress = scope.ServiceProvider.GetRequiredService<IProgressService>();
            await progress.MarkPlanItemCompleteAsync(first.Id, 2);
        }
        var freshlyLogged = await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey);
        Assert.Equal(2, Assert.Single(freshlyLogged, row => !row.IsArchived).MinutesSpent);
        Assert.Equal(9, Assert.Single(freshlyLogged, row => row.Id == oldRow.Id).MinutesSpent);

        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.True(await lease.Service.UpdateProgressAsync(LocalDate, first.Id, 5));
            Assert.True(await lease.Service.UpdateProgressAsync(LocalDate, first.Id, 2));
        }
        var unchanged = Assert.Single((await GenerateAsync(fixture, NativeSliceFixture.UserA)).Items);
        Assert.Equal(5, unchanged.MinutesSpent);
        Assert.False(unchanged.IsCompleted);
        var after = await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey);
        Assert.Equal(active.Id, Assert.Single(after, row => !row.IsArchived).Id);
        Assert.Equal(9, Assert.Single(after, row => row.Id == oldRow.Id).MinutesSpent);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.Equal(5, Assert.Single((await lease.Service.GetTodayAsync())!.Items).MinutesSpent);
        Assert.Equal(3, (await HistoryAsync(fixture)).Count);
    }

    [Fact]
    public async Task ResetAndLegacyClearArchiveOnlyCurrentRowsWithoutDiscardingHistory()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Reading", fixture.ResourceId(NativeSliceFixture.UserA), null, 15, 1));
        var item = Assert.Single((await GenerateAsync(fixture, NativeSliceFixture.UserA)).Items);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.True(await lease.Service.UpdateProgressAsync(LocalDate, item.Id, 6));
            await lease.Service.ResetTodayAsync();
            Assert.Null(await lease.Service.GetTodayAsync());
            await lease.Service.ResetTodayAsync();
        }
        var archived = Assert.Single(await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey));
        Assert.True(archived.IsArchived);
        Assert.Equal(6, archived.MinutesSpent);

        var fresh = Assert.Single((await GenerateAsync(fixture, NativeSliceFixture.UserA)).Items);
        Assert.Equal(item.Id, fresh.Id);
        Assert.Equal(0, fresh.MinutesSpent);
        using (var scope = fixture.Scope())
        {
            var progress = scope.ServiceProvider.GetRequiredService<IProgressService>();
            await progress.ClearCachedPlanAsync(DateKey);
            await progress.ClearCachedPlanAsync(DateKey);
            Assert.Null(await progress.GetCachedPlanAsync(DateKey));
        }
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.Null(await lease.Service.GetTodayAsync());
        var rows = await RowsAsync(fixture, NativeSliceFixture.UserA, DateKey);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsArchived));
        Assert.Equal(6, Assert.Single(rows, row => row.Id == archived.Id).MinutesSpent);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(2, (await HistoryAsync(fixture)).Count);
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IIndex ActivePlanIndex(ApplicationDbContext db) =>
        Assert.Single(db.Model.FindEntityType(typeof(DailyPlanCompletion))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(
                ["UserProfileId", "Date", "PlanItemId"]));

    private static async Task<TodaysPlanDto> GenerateAsync(NativeSliceFixture fixture, string owner)
    {
        using var lease = fixture.PlanLease(owner);
        return await lease.Service.GenerateTodayAsync(new GenerateTodaysPlanRequest
        {
            Strategy = "deterministic"
        });
    }

    private static async Task<List<DailyPlanCompletion>> RowsAsync(
        NativeSliceFixture fixture, string owner, DateTime date)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == owner && row.Date == date)
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    private static async Task<List<ActivityLogEntry>> HistoryAsync(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var weeks = await scope.ServiceProvider.GetRequiredService<IProgressService>()
            .GetActivityLogAsync(DateKey, DateKey);
        return weeks.SelectMany(week => week.Days)
            .SelectMany(day => day.Plans)
            .SelectMany(plan => plan.Items)
            .ToList();
    }

    private static DailyPlanCompletion Row(
        string id, string owner, DateTime date, string itemId, int minutes,
        bool completed, bool archived = false) => new()
    {
        Id = id,
        UserProfileId = owner,
        Date = date,
        PlanItemId = itemId,
        ActivityType = "Reading",
        IsCompleted = completed,
        IsArchived = archived,
        CompletedAt = completed ? date.AddHours(10) : null,
        MinutesSpent = minutes,
        EstimatedMinutes = 15,
        Priority = 1,
        CreatedAt = date.AddHours(8),
        UpdatedAt = date.AddHours(10)
    };

    private static ApplicationDbContext MigrationContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection, options =>
                options.MigrationsAssembly(typeof(SqliteMigration).Assembly.GetName().Name))
            .Options);

    private static LegacyPlanContext LegacyContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<LegacyPlanContext>().UseSqlite(connection).Options);

    private static async Task<string?> IndexSqlAsync(SqliteConnection connection) =>
        await ScalarAsync(connection, $"""
            SELECT sql FROM sqlite_master WHERE type = 'index'
            AND tbl_name = 'DailyPlanCompletion' AND name = '{ActiveIndex}';
            """);

    private static Task<string?> TrackingTriggerSqlAsync(SqliteConnection connection) =>
        ScalarAsync(connection, """
            SELECT sql FROM sqlite_master WHERE type = 'trigger'
            AND tbl_name = 'DailyPlanCompletion' AND name = 'ScratchPlanTrackingTrigger';
            """);

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }

    private sealed class LegacyPlanContext(DbContextOptions<LegacyPlanContext> options)
        : DbContext(options)
    {
        public DbSet<DailyPlanCompletion> Completions => Set<DailyPlanCompletion>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var plan = modelBuilder.Entity<DailyPlanCompletion>();
            plan.ToTable("DailyPlanCompletion").HasKey(row => row.Id);
            plan.Property(row => row.Id).ValueGeneratedNever();
            plan.Ignore(row => row.IsArchived);
            plan.HasIndex(row => new { row.UserProfileId, row.Date, row.PlanItemId })
                .IsUnique();
        }
    }
}
