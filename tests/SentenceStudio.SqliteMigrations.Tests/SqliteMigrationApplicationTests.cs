using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.SqliteMigrations.Tests;

public sealed class SqliteMigrationApplicationTests
{
    private const string BeforePlanRebuildMigration = "20260321133200_AddYouTubeChannelMonitoring";
    private const string PlanRebuildMigration = "20260322012812_SyncDailyPlanAndUserActivity";
    private const string PreviousMigration = "20260328192206_AddMissingVocabularyWordLanguageColumn";
    private const string NarrativeMigration = "20260404185452_AddNarrativeJson";
    private const string PassiveExposureMigration = "20260415024019_AddPassiveExposureFields";
    private const string IndexMigration = "20260621193737_AddVocabularyDuplicateLookupIndexes";
    private const string StreamOwnerMigration = "20260927100455_AddStreamHistoryOwnerScope";
    private const string ArchiveMigration = "20260928050905_ArchiveDisplacedPlanItems";

    [Fact]
    public async Task FreshDatabase_AppliesEveryDiscoveredMigration()
    {
        using var scratch = new ScratchDatabase();
        await using var db = CreateContext(scratch.ConnectionString);
        var discovered = db.Database.GetMigrations().ToArray();
        Assert.Contains(NarrativeMigration, discovered);
        Assert.Contains(IndexMigration, discovered);
        Assert.Contains(StreamOwnerMigration, discovered);
        Assert.Contains(ArchiveMigration, discovered);

        await db.Database.MigrateAsync();

        Assert.Equal(discovered, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await AssertSchemaAsync(scratch.ConnectionString);
        await AssertModeledColumnsAsync(db, scratch.ConnectionString);
    }

    [Fact]
    public async Task PreviousDatabase_UpgradesWithoutLosingOfflineRowsOrMigrationHistory()
    {
        using var scratch = new ScratchDatabase();
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(PreviousMigration);
            var applied = (await previous.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.Equal(PreviousMigration, applied.Last());
            Assert.DoesNotContain(NarrativeMigration, applied);
            Assert.DoesNotContain(IndexMigration, applied);
            Assert.Contains(NarrativeMigration, await previous.Database.GetPendingMigrationsAsync());
            Assert.Contains(IndexMigration, await previous.Database.GetPendingMigrationsAsync());
        }

        var recordedAt = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        int streamId;
        await using (var legacy = new LegacyStreamHistoryContext(
            new DbContextOptionsBuilder<LegacyStreamHistoryContext>()
                .UseSqlite(scratch.ConnectionString).Options))
        {
            var stream = new StreamHistory
            {
                Phrase = "offline recording",
                AudioFilePath = "offline-recording.mp3",
                Duration = 2.5,
                CreatedAt = recordedAt,
                UpdatedAt = recordedAt
            };
            legacy.StreamHistories.Add(stream);
            await legacy.SaveChangesAsync();
            streamId = stream.Id;
        }

        var planId = Guid.NewGuid().ToString();
        await using (var legacy = new LegacyPlanContext(
            new DbContextOptionsBuilder<LegacyPlanContext>()
                .UseSqlite(scratch.ConnectionString).Options))
        {
            var plan = new DailyPlanCompletion
            {
                Id = planId,
                UserProfileId = "offline-owner",
                Date = recordedAt.Date,
                PlanItemId = "assigned-vocabulary-quiz",
                ActivityType = "VocabularyQuiz",
                TitleKey = "PlanItemVocabReviewTitle",
                DescriptionKey = "PlanItemVocabReviewDescription",
                CreatedAt = recordedAt,
                UpdatedAt = recordedAt
            };
            legacy.PlanCompletions.Add(plan);
            legacy.Entry(plan).Property<string>("NarrativeJson").CurrentValue =
                "{\"offlineNarrative\":\"preserved\"}";
            await legacy.SaveChangesAsync();
        }

        await using (var upgraded = CreateContext(scratch.ConnectionString))
        {
            await upgraded.Database.MigrateAsync();
            Assert.Equal(upgraded.Database.GetMigrations(),
                await upgraded.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
            Assert.False(upgraded.Database.HasPendingModelChanges());

            var stream = await upgraded.StreamHistories.AsNoTracking()
                .SingleAsync(row => row.Id == streamId);
            Assert.Equal("offline recording", stream.Phrase);
            Assert.Equal("offline-recording.mp3", stream.AudioFilePath);
            Assert.Equal(2.5, stream.Duration);
            Assert.Equal(recordedAt, stream.CreatedAt);
            Assert.Null(stream.UserProfileId);

            var plan = await upgraded.DailyPlanCompletions.AsNoTracking()
                .SingleAsync(row => row.Id == planId);
            Assert.Equal("offline-owner", plan.UserProfileId);
            Assert.Equal("assigned-vocabulary-quiz", plan.PlanItemId);
            Assert.Equal(recordedAt.Date, plan.Date);
            Assert.False(plan.IsArchived);
            var narrative = await upgraded.DailyPlanCompletions
                .Where(row => row.Id == planId)
                .Select(row => EF.Property<string>(row, "NarrativeJson"))
                .SingleAsync();
            Assert.Equal("{\"offlineNarrative\":\"preserved\"}", narrative);
        }

        await AssertSchemaAsync(scratch.ConnectionString);
    }

    [Fact]
    public async Task LegacyPatchedNarrativeSurvivesPlanTableRebuildAndLaterMigrations()
    {
        using var scratch = new ScratchDatabase();
        var narrative = "{\"legacy\":\"keep this local plan\"}";
        var planItemId = Guid.NewGuid().ToString();
        var recordedAt = new DateTime(2026, 3, 22, 12, 30, 0, DateTimeKind.Utc);

        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(BeforePlanRebuildMigration);
            Assert.Equal(BeforePlanRebuildMigration,
                (await previous.Database.GetAppliedMigrationsAsync()).Last());

            var legacyPatch = new MigrationBuilder(previous.Database.ProviderName!);
            legacyPatch.AddColumn<string>(
                name: "NarrativeJson",
                table: "DailyPlanCompletion",
                type: "TEXT",
                nullable: true);
            var generatedSql = Assert.Single(previous.GetService<IMigrationsSqlGenerator>()
                .Generate(legacyPatch.Operations));
            await previous.Database.ExecuteSqlRawAsync(generatedSql.CommandText);
        }

        await using (var legacy = new LegacyIntPlanContext(
            new DbContextOptionsBuilder<LegacyIntPlanContext>()
                .UseSqlite(scratch.ConnectionString).Options))
        {
            legacy.PlanCompletions.Add(new LegacyIntPlan
            {
                Date = recordedAt.Date,
                PlanItemId = planItemId,
                ActivityType = "VocabularyQuiz",
                TitleKey = "PlanItemVocabReviewTitle",
                DescriptionKey = "PlanItemVocabReviewDescription",
                Rationale = "offline plan",
                NarrativeJson = narrative,
                CreatedAt = recordedAt,
                UpdatedAt = recordedAt
            });
            await legacy.SaveChangesAsync();
        }

        Assert.Equal(narrative, await ReadNarrativeAsync(scratch.ConnectionString, planItemId));

        await using (var rebuilt = CreateContext(scratch.ConnectionString))
        {
            await rebuilt.Database.MigrateAsync(PlanRebuildMigration);
            Assert.Equal(PlanRebuildMigration,
                (await rebuilt.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Equal(narrative, await ReadNarrativeAsync(scratch.ConnectionString, planItemId));

            await rebuilt.Database.MigrateAsync();
            Assert.Equal(rebuilt.Database.GetMigrations(),
                await rebuilt.Database.GetAppliedMigrationsAsync());
            Assert.False(rebuilt.Database.HasPendingModelChanges());
        }
        Assert.Equal(narrative, await ReadNarrativeAsync(scratch.ConnectionString, planItemId));
    }

    [Fact]
    public async Task ImmediatelyPreviousDatabase_AppliesArchiveWithoutLosingOwnedRows()
    {
        using var scratch = new ScratchDatabase();
        var recordedAt = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        var planId = Guid.NewGuid().ToString();
        int streamId;
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(StreamOwnerMigration);
            Assert.Equal(StreamOwnerMigration,
                (await previous.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Contains(ArchiveMigration, await previous.Database.GetPendingMigrationsAsync());
            var stream = new StreamHistory
            {
                UserProfileId = "offline-owner",
                Phrase = "owned recording",
                CreatedAt = recordedAt,
                UpdatedAt = recordedAt
            };
            previous.StreamHistories.Add(stream);
            await previous.SaveChangesAsync();
            streamId = stream.Id;
        }

        await using (var legacy = new LegacyPlanContext(
            new DbContextOptionsBuilder<LegacyPlanContext>()
                .UseSqlite(scratch.ConnectionString).Options))
        {
            legacy.PlanCompletions.Add(new DailyPlanCompletion
            {
                Id = planId,
                UserProfileId = "offline-owner",
                Date = recordedAt.Date,
                PlanItemId = "assigned-vocabulary-quiz",
                ActivityType = "VocabularyQuiz",
                TitleKey = "PlanItemVocabReviewTitle",
                DescriptionKey = "PlanItemVocabReviewDescription",
                CreatedAt = recordedAt,
                UpdatedAt = recordedAt
            });
            await legacy.SaveChangesAsync();
        }

        await using var upgraded = CreateContext(scratch.ConnectionString);
        await upgraded.Database.MigrateAsync();
        Assert.Equal(upgraded.Database.GetMigrations(),
            await upgraded.Database.GetAppliedMigrationsAsync());
        Assert.False(upgraded.Database.HasPendingModelChanges());

        var plan = await upgraded.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.Id == planId);
        Assert.Equal("offline-owner", plan.UserProfileId);
        Assert.False(plan.IsArchived);
        var streamAfter = await upgraded.StreamHistories.AsNoTracking()
            .SingleAsync(row => row.Id == streamId);
        Assert.Equal("owned recording", streamAfter.Phrase);
        Assert.Equal("offline-owner", streamAfter.UserProfileId);

        upgraded.DailyPlanCompletions.Add(new DailyPlanCompletion
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = plan.UserProfileId,
            Date = plan.Date,
            PlanItemId = plan.PlanItemId,
            ActivityType = plan.ActivityType,
            TitleKey = plan.TitleKey,
            DescriptionKey = plan.DescriptionKey,
            IsArchived = true,
            CreatedAt = recordedAt,
            UpdatedAt = recordedAt
        });
        await upgraded.SaveChangesAsync();
        Assert.Equal(2, await upgraded.DailyPlanCompletions
            .CountAsync(row => row.UserProfileId == plan.UserProfileId
                && row.Date == plan.Date && row.PlanItemId == plan.PlanItemId));

        await AssertSchemaAsync(scratch.ConnectionString);
        await AssertModeledColumnsAsync(upgraded, scratch.ConnectionString);
    }

    [Fact]
    public async Task ExposureColumnsAndValuesSurviveStreakColumnRebuild()
    {
        using var scratch = new ScratchDatabase();
        await using (var before = CreateContext(scratch.ConnectionString))
            await before.Database.MigrateAsync(PassiveExposureMigration);

        var wordId = Guid.NewGuid().ToString();
        var progressId = Guid.NewGuid().ToString();
        var exposedAt = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        await using (var legacy = new LegacyPassiveProgressContext(
            new DbContextOptionsBuilder<LegacyPassiveProgressContext>()
                .UseSqlite(scratch.ConnectionString).Options))
        {
            legacy.Words.Add(new VocabularyWord
            {
                Id = wordId,
                TargetLanguageTerm = "target",
                CreatedAt = exposedAt,
                UpdatedAt = exposedAt
            });
            legacy.Progresses.Add(new VocabularyProgress
            {
                Id = progressId,
                VocabularyWordId = wordId,
                UserId = "offline-owner",
                ExposureCount = 7,
                LastExposedAt = exposedAt,
                CurrentStreak = 2,
                CreatedAt = exposedAt,
                UpdatedAt = exposedAt
            });
            await legacy.SaveChangesAsync();
        }

        await using var upgraded = CreateContext(scratch.ConnectionString);
        await upgraded.Database.MigrateAsync();
        var progress = await upgraded.VocabularyProgresses.AsNoTracking()
            .SingleAsync(row => row.Id == progressId);
        Assert.Equal(7, progress.ExposureCount);
        Assert.Equal(exposedAt, progress.LastExposedAt);
        Assert.Equal(2, progress.CurrentStreak);
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
    }

    private static async Task AssertModeledColumnsAsync(
        ApplicationDbContext db, string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var missingOrMismatched = new List<string>();

        foreach (var table in db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table.Name.Replace("\"", "\"\"")}\")";
            await using var reader = await command.ExecuteReaderAsync();
            var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
                actual.Add(reader.GetString(1), reader.GetString(2));

            foreach (var column in table.Columns)
            {
                if (!actual.TryGetValue(column.Name, out var type))
                    missingOrMismatched.Add($"{table.Name}.{column.Name}: missing");
                else if (!string.Equals(type, column.StoreType, StringComparison.OrdinalIgnoreCase))
                    missingOrMismatched.Add(
                        $"{table.Name}.{column.Name}: stored {type}, model expects {column.StoreType}");
            }
        }

        Assert.True(missingOrMismatched.Count == 0,
            "Fresh EF migrations must materialize every mobile-model column:"
            + Environment.NewLine + string.Join(Environment.NewLine, missingOrMismatched));
    }

    private static ApplicationDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString).Options);

    private static async Task<string?> ReadNarrativeAsync(
        string connectionString, string planItemId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"NarrativeJson\" FROM \"DailyPlanCompletion\" WHERE \"PlanItemId\" = $planItemId";
        command.Parameters.AddWithValue("$planItemId", planItemId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task AssertSchemaAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var column = connection.CreateCommand();
        column.CommandText = "PRAGMA table_info('StreamHistory')";
        await using (var reader = await column.ExecuteReaderAsync())
        {
            var found = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) != "UserProfileId")
                    continue;
                Assert.Equal("TEXT", reader.GetString(2));
                Assert.Equal(0, reader.GetInt32(3));
                found = true;
            }
            Assert.True(found, "StreamHistory must have a nullable TEXT owner column.");
        }

        await using var planColumns = connection.CreateCommand();
        planColumns.CommandText = "PRAGMA table_info('DailyPlanCompletion')";
        await using (var reader = await planColumns.ExecuteReaderAsync())
        {
            var foundNarrative = false;
            var foundArchive = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == "NarrativeJson")
                {
                    Assert.Equal("TEXT", reader.GetString(2));
                    Assert.Equal(0, reader.GetInt32(3));
                    foundNarrative = true;
                }
                if (reader.GetString(1) == "IsArchived")
                {
                    Assert.Equal("INTEGER", reader.GetString(2));
                    Assert.Equal(1, reader.GetInt32(3));
                    Assert.Equal("0", reader.GetString(4));
                    foundArchive = true;
                }
            }
            Assert.True(foundNarrative, "DailyPlanCompletion must retain its nullable narrative column.");
            Assert.True(foundArchive, "DailyPlanCompletion must have the archive column.");
        }

        await AssertIndexAsync(connection, "IX_StreamHistory_UserProfileId_CreatedAt",
            "UserProfileId", "CreatedAt");
        await AssertIndexAsync(connection, "IX_LearningResource_UserProfileId",
            "UserProfileId");
        await AssertIndexAsync(connection,
            "IX_ResourceVocabularyMapping_ResourceId_VocabularyWordId",
            "ResourceId", "VocabularyWordId");
        await AssertIndexAsync(connection,
            "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
            "UserProfileId", "Date", "PlanItemId");

        await using var archiveIndex = connection.CreateCommand();
        archiveIndex.CommandText =
            "SELECT sql FROM sqlite_schema WHERE type='index' AND name='IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId'";
        var archiveIndexSql = Assert.IsType<string>(await archiveIndex.ExecuteScalarAsync());
        Assert.Contains("WHERE \"IsArchived\" = 0", archiveIndexSql);

        await using var legacyIdentity = connection.CreateCommand();
        legacyIdentity.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'AspNetUsers'";
        Assert.Equal(1L, await legacyIdentity.ExecuteScalarAsync());
    }

    private static async Task AssertIndexAsync(
        SqliteConnection connection, string name, params string[] columns)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info('{name}')";
        await using var reader = await command.ExecuteReaderAsync();
        var actual = new List<string>();
        while (await reader.ReadAsync())
            actual.Add(reader.GetString(2));
        Assert.Equal(columns, actual);
    }

    private sealed class LegacyStreamHistoryContext(
        DbContextOptions<LegacyStreamHistoryContext> options) : DbContext(options)
    {
        public DbSet<StreamHistory> StreamHistories => Set<StreamHistory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StreamHistory>().ToTable("StreamHistory").HasKey(row => row.Id);
            modelBuilder.Entity<StreamHistory>().Ignore(row => row.UserProfileId);
        }
    }

    private sealed class LegacyPlanContext(
        DbContextOptions<LegacyPlanContext> options) : DbContext(options)
    {
        public DbSet<DailyPlanCompletion> PlanCompletions => Set<DailyPlanCompletion>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DailyPlanCompletion>().ToTable("DailyPlanCompletion")
                .HasKey(row => row.Id);
            modelBuilder.Entity<DailyPlanCompletion>().Ignore(row => row.IsArchived);
        }
    }

    private sealed class LegacyIntPlan
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string PlanItemId { get; set; } = string.Empty;
        public string ActivityType { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
        public int MinutesSpent { get; set; }
        public int EstimatedMinutes { get; set; }
        public int Priority { get; set; }
        public string TitleKey { get; set; } = string.Empty;
        public string DescriptionKey { get; set; } = string.Empty;
        public string Rationale { get; set; } = string.Empty;
        public string? NarrativeJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class LegacyIntPlanContext(
        DbContextOptions<LegacyIntPlanContext> options) : DbContext(options)
    {
        public DbSet<LegacyIntPlan> PlanCompletions => Set<LegacyIntPlan>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LegacyIntPlan>().ToTable("DailyPlanCompletion")
                .HasKey(row => row.Id);
        }
    }

    private sealed class LegacyPassiveProgressContext(
        DbContextOptions<LegacyPassiveProgressContext> options) : DbContext(options)
    {
        public DbSet<VocabularyWord> Words => Set<VocabularyWord>();
        public DbSet<VocabularyProgress> Progresses => Set<VocabularyProgress>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var word = modelBuilder.Entity<VocabularyWord>();
            word.ToTable("VocabularyWord").HasKey(row => row.Id);
            word.Ignore(row => row.LexicalUnitType);
            word.Ignore(row => row.PartOfSpeech);
            word.Ignore(row => row.LearningResources);
            word.Ignore(row => row.ResourceMappings);
            word.Ignore(row => row.ExampleSentences);

            var progress = modelBuilder.Entity<VocabularyProgress>();
            progress.ToTable("VocabularyProgress").HasKey(row => row.Id);
            progress.Ignore(row => row.QuizRecognitionDemonstrations);
            progress.Ignore(row => row.QuizProductionDemonstrations);
            progress.Ignore(row => row.LearningContexts);
            progress.HasOne(row => row.VocabularyWord).WithMany()
                .HasForeignKey(row => row.VocabularyWordId);
        }
    }

    private sealed class ScratchDatabase : IDisposable
    {
        private readonly DirectoryInfo _directory =
            Directory.CreateTempSubdirectory("SentenceStudio.SqliteMigrations.Tests.");

        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory.FullName, "scratch.sqlite"),
            Pooling = false
        }.ToString();

        public void Dispose()
        {
            var database = Path.Combine(_directory.FullName, "scratch.sqlite");
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var path = database + suffix;
                if (File.Exists(path))
                    File.Delete(path);
            }
            _directory.Delete();
        }
    }
}
