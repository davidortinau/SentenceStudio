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
    private const string WordOwnerMigration = "20261020095243_AddVocabularyWordOwner";
    private const string Phase2aMigration = "20261103015116_SyncTextIdsPhase2a";
    private const string Phase2bMigration = "20261103151517_SyncTextIdsPhase2b";
    private const string Phase3Migration = "20261103230000_SyncOwnersPhase3";
    private const string LookupMigration = "20261104153858_AddVocabularyLookup";

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
        Assert.Contains(WordOwnerMigration, discovered);

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
        // The old schema numbers audio history rows, so the row is written with SQL.
        await using (var legacy = CreateContext(scratch.ConnectionString))
        {
            await legacy.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"StreamHistory\" (\"Phrase\", \"AudioFilePath\", \"Duration\", \"CreatedAt\", \"UpdatedAt\") VALUES ({0}, {1}, {2}, {3}, {4})",
                "offline recording", "offline-recording.mp3", 2.5, recordedAt, recordedAt);
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

            // Phase 2a gives every row a new text ID (old numbers are not reused).
            var stream = await upgraded.StreamHistories.AsNoTracking()
                .SingleAsync(row => row.Phrase == "offline recording");
            Assert.True(Guid.TryParse(stream.Id, out _));
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
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(StreamOwnerMigration);
            Assert.Equal(StreamOwnerMigration,
                (await previous.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Contains(ArchiveMigration, await previous.Database.GetPendingMigrationsAsync());
            await previous.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"StreamHistory\" (\"UserProfileId\", \"Phrase\", \"Duration\", \"CreatedAt\", \"UpdatedAt\") VALUES ({0}, {1}, 0, {2}, {3})",
                "offline-owner", "owned recording", recordedAt, recordedAt);
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
            .SingleAsync(row => row.Phrase == "owned recording");
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
    public async Task ImmediatelyPreviousDatabase_AddsWordOwner_AndBackfillStampsOnlySingleOwnerWords()
    {
        using var scratch = new ScratchDatabase();
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(ArchiveMigration);
            Assert.Equal(ArchiveMigration,
                (await previous.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Contains(WordOwnerMigration, await previous.Database.GetPendingMigrationsAsync());
        }

        // Offline rows written before the owner column existed (raw SQL: the current
        // model already maps UserProfileId).
        await using (var connection = new SqliteConnection(scratch.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Word owners go only to real profiles (f4a4c1c5), so both learners have one.
            command.CommandText = """
                INSERT INTO "UserProfile" ("Id", "Name", "NativeLanguage", "TargetLanguage", "PreferredSessionMinutes", "CreatedAt")
                VALUES ('owner-a', 'A', 'English', 'Korean', 20, '2026-09-02 10:00:00'),
                       ('owner-b', 'B', 'English', 'Korean', 20, '2026-09-02 10:00:00');
                INSERT INTO LearningResource (Id, UserProfileId, CreatedAt, UpdatedAt, IsSmartResource)
                VALUES ('res-a', 'owner-a', '2026-09-02 10:00:00', '2026-09-02 10:00:00', 0),
                       ('res-b', 'owner-b', '2026-09-02 10:00:00', '2026-09-02 10:00:00', 0);
                INSERT INTO VocabularyWord (Id, TargetLanguageTerm, NativeLanguageTerm, MnemonicText, CreatedAt, UpdatedAt)
                VALUES ('word-a', '고양이', 'cat', 'A''s cat story', '2026-09-02 10:00:00', '2026-09-02 10:00:00'),
                       ('word-shared', '개', 'dog', NULL, '2026-09-02 10:00:00', '2026-09-02 10:00:00'),
                       ('word-unlinked', '새', 'bird', 'whose story?', '2026-09-02 10:00:00', '2026-09-02 10:00:00');
                INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId)
                VALUES ('map-a', 'res-a', 'word-a'),
                       ('map-shared-a', 'res-a', 'word-shared'),
                       ('map-shared-b', 'res-b', 'word-shared');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var upgraded = CreateContext(scratch.ConnectionString);
        await upgraded.Database.MigrateAsync();
        Assert.Equal(upgraded.Database.GetMigrations(),
            await upgraded.Database.GetAppliedMigrationsAsync());
        Assert.False(upgraded.Database.HasPendingModelChanges());
        Assert.All(await upgraded.VocabularyWords.AsNoTracking().ToListAsync(),
            word => Assert.Null(word.UserProfileId));

        var result = await VocabularyWordOwnership.BackfillAsync(upgraded);
        Assert.Equal("owner-a", Assert.Single(result.Stamped, pair => pair.Key == "word-a").Value);
        Assert.Single(result.Stamped);
        Assert.Equal(1, result.Shared);
        Assert.Equal(1, result.Unlinked);

        var words = await upgraded.VocabularyWords.AsNoTracking()
            .ToDictionaryAsync(word => word.Id);
        Assert.Equal("owner-a", words["word-a"].UserProfileId);
        Assert.Equal("A's cat story", words["word-a"].MnemonicText);
        Assert.Null(words["word-shared"].UserProfileId);
        Assert.Null(words["word-unlinked"].UserProfileId);
        Assert.Equal("whose story?", words["word-unlinked"].MnemonicText);
        Assert.Equal(3, await upgraded.ResourceVocabularyMappings.CountAsync());

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

    /// <summary>
    /// Phase 2a of syncing every learner table (Captain 2026-11-02, docs/sync-all-tables-plan.md):
    /// number IDs become text IDs that are unique on every store, answers keep their session and
    /// pair, conversation memories gain their owner, and nothing is lost in either direction.
    /// </summary>
    [Fact]
    public async Task SyncTextIds_ConvertsNumberIdsKeepingEveryRowAndLink_AndDownRestoresThem()
    {
        using var scratch = new ScratchDatabase();
        var at = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        const string owner = "owner-a";
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(WordOwnerMigration);
            foreach (var id in new[] { "w1", "w2", "w3" })
                previous.VocabularyWords.Add(new VocabularyWord
                {
                    // Every word has an owner (a save refuses one without).
                    Id = id, UserProfileId = owner, TargetLanguageTerm = id, CreatedAt = at, UpdatedAt = at
                });
            previous.Conversations.Add(new Conversation { Id = "conv-1", UserProfileId = owner, CreatedAt = at });
            await previous.SaveChangesAsync();
            foreach (var sql in new[]
            {
                "INSERT INTO \"MinimalPair\" (\"Id\", \"UserId\", \"VocabularyWordAId\", \"VocabularyWordBId\", \"CreatedAt\", \"UpdatedAt\") VALUES (7, 'owner-a', 'w1', 'w2', '2026-11-01 09:00:00', '2026-11-01 09:00:00')",
                "INSERT INTO \"MinimalPair\" (\"Id\", \"UserId\", \"VocabularyWordAId\", \"VocabularyWordBId\", \"CreatedAt\", \"UpdatedAt\") VALUES (8, 'owner-a', 'w2', 'w3', '2026-11-01 09:01:00', '2026-11-01 09:01:00')",
                "INSERT INTO \"MinimalPairSession\" (\"Id\", \"UserId\", \"Mode\", \"StartedAt\", \"CreatedAt\", \"UpdatedAt\") VALUES (3, 'owner-a', 'Focus', '2026-11-01 09:00:00', '2026-11-01 09:00:00', '2026-11-01 09:00:00')",
                "INSERT INTO \"MinimalPairAttempt\" (\"UserId\", \"SessionId\", \"PairId\", \"PromptWordId\", \"SelectedWordId\", \"IsCorrect\", \"SequenceNumber\", \"CreatedAt\") VALUES ('owner-a', 3, 7, 'w1', 'w1', 1, 1, '2026-11-01 09:02:00')",
                "INSERT INTO \"MinimalPairAttempt\" (\"UserId\", \"SessionId\", \"PairId\", \"PromptWordId\", \"SelectedWordId\", \"IsCorrect\", \"SequenceNumber\", \"CreatedAt\") VALUES ('owner-a', 3, 8, 'w2', 'w3', 0, 2, '2026-11-01 09:03:00')",
                "INSERT INTO \"ConversationMemoryState\" (\"ConversationId\", \"SerializedState\", \"CreatedAt\", \"UpdatedAt\") VALUES ('conv-1', 'state', '2026-11-01 09:00:00', '2026-11-01 09:00:00')",
                "INSERT INTO \"StreamHistory\" (\"UserProfileId\", \"Phrase\", \"Duration\", \"CreatedAt\", \"UpdatedAt\") VALUES ('owner-a', 'hello', 1.5, '2026-11-01 09:00:00', '2026-11-01 09:00:00')",
            })
                await previous.Database.ExecuteSqlRawAsync(sql);
        }

        await using (var upgraded = CreateContext(scratch.ConnectionString))
        {
            await upgraded.Database.MigrateAsync();
            Assert.False(upgraded.Database.HasPendingModelChanges());

            var pairs = await upgraded.MinimalPairs.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
            Assert.Equal(["mp:owner-a:w1:w2", "mp:owner-a:w2:w3"], pairs.Select(p => p.Id));
            Assert.Equal(pairs.Select(p => SentenceStudio.Services.SyncIds.MinimalPair(
                p.UserId, p.VocabularyWordAId, p.VocabularyWordBId)), pairs.Select(p => p.Id));
            var session = await upgraded.MinimalPairSessions.AsNoTracking().SingleAsync();
            Assert.True(Guid.TryParse(session.Id, out _));
            var attempts = await upgraded.MinimalPairAttempts.AsNoTracking()
                .OrderBy(a => a.SequenceNumber).ToListAsync();
            Assert.Equal(2, attempts.Count);
            Assert.All(attempts, a => Assert.Equal(session.Id, a.SessionId));
            Assert.Equal(["mp:owner-a:w1:w2", "mp:owner-a:w2:w3"], attempts.Select(a => a.PairId));
            Assert.Equal([true, false], attempts.Select(a => a.IsCorrect));
            Assert.All(attempts, a => Assert.True(Guid.TryParse(a.Id, out _)));

            var memory = await upgraded.ConversationMemoryStates.AsNoTracking().SingleAsync();
            Assert.Equal(("cm:conv-1", owner), (memory.Id, memory.UserProfileId));
            Assert.Equal(SentenceStudio.Services.SyncIds.ConversationMemory("conv-1"), memory.Id);

            var stream = await upgraded.StreamHistories.AsNoTracking().SingleAsync();
            Assert.True(Guid.TryParse(stream.Id, out _));
            Assert.Equal(("hello", owner, 1.5), (stream.Phrase, stream.UserProfileId, stream.Duration));

            // Deleting a pair still removes its answers (the cascade survived the rebuild).
            upgraded.MinimalPairs.Remove(pairs[1]);
            await upgraded.SaveChangesAsync();
            Assert.Equal(1, await upgraded.MinimalPairAttempts.CountAsync());

            var migrator = upgraded.GetService<IMigrator>();
            await migrator.MigrateAsync(WordOwnerMigration);
        }

        await using var connection = new SqliteConnection(scratch.ConnectionString);
        await connection.OpenAsync();
        async Task<string> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        }
        Assert.Equal("1", await Scalar("SELECT \"Id\" FROM \"MinimalPair\""));
        Assert.Equal("1|1|1", await Scalar(
            "SELECT \"SessionId\" || '|' || \"PairId\" || '|' || \"IsCorrect\" FROM \"MinimalPairAttempt\""));
        Assert.Equal("integer", await Scalar("SELECT lower(typeof(\"Id\")) FROM \"StreamHistory\""));
        Assert.Equal("1", await Scalar("SELECT count(*) FROM \"ConversationMemoryState\""));
        Assert.Equal("0", await Scalar(
            "SELECT count(*) FROM pragma_table_info('ConversationMemoryState') WHERE name = 'UserProfileId'"));
        Assert.Equal("ok", await Scalar("PRAGMA foreign_key_check") is "" ? "ok" : "violations");
    }

    /// <summary>
    /// Phase 2b (Captain 2026-11-03, docs/sync-all-tables-plan.md): activity resume rows get text
    /// IDs. The in-progress (else latest) run of each launch gets the shared run ID, older runs
    /// keep their history, receipts become device-only and still point at their quiz row, and a
    /// bare saved snapshot is wrapped in the one saved format. Down restores every row.
    /// </summary>
    [Fact]
    public async Task SyncTextIdsPhase2b_GivesRunIds_KeepsHistoryAndReceiptLinks_WrapsBareSnapshots_AndDownRestoresThem()
    {
        using var scratch = new ScratchDatabase();
        const string owner = "owner-a";
        const string webKey = "plan=p1|focus=|res=|due=false|skill=";
        const string nativeKey = "plan=|focus=|res=r1|due=false|skill=";
        const string abandonedKey = "plan=p2|focus=|res=|due=false|skill=";
        const string bareSnapshot = "{\"planItemId\":\"p1\",\"batchPool\":[{\"wordId\":\"w1\"}],\"roundWordOrder\":[\"w1\"],\"totalTurns\":3}";
        const string nativeState = "{\"version\":1,\"userId\":\"owner-a\",\"planDate\":\"2026-11-02\",\"launchContextKey\":\"plan=|focus=|res=r1|due=false|skill=\",\"planItemRowId\":\"row-1\",\"turnId\":\"0123456789abcdef0123456789abcdef\",\"displayDirection\":\"TargetToNative\",\"snapshot\":{\"batchPool\":[]}}";
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(Phase2aMigration);
            string Row(int id, string type, string key, string json, string status, string updated) =>
                "INSERT INTO \"ActivitySession\" (\"Id\", \"UserId\", \"ActivityType\", \"LaunchContextKey\", \"StateJson\", \"Status\", \"StartedAt\", \"UpdatedAt\") "
                + $"VALUES ({id}, '{owner}', '{type}', '{key}', '{json.Replace("'", "''")}', '{status}', '{updated}', '{updated}')";
            foreach (var sql in new[]
            {
                Row(1, "VocabularyReview", webKey, bareSnapshot, "InProgress", "2026-11-02 08:00:00"),
                Row(2, "VocabularyReview", webKey, bareSnapshot, "Completed", "2026-11-01 08:00:00"),
                Row(3, "VocabularyReview", nativeKey, nativeState, "InProgress", "2026-11-02 09:00:00"),
                Row(4, ActivitySessionTypes.NativeAttemptReceipt, "[\"k\",\"t1\"]", "{\"version\":1,\"sourceSessionId\":3}", "InProgress", "2026-11-02 09:01:00"),
                Row(5, ActivitySessionTypes.NativeSentenceReceipt, "[\"k\",\"t2\",\"s\"]", "{\"version\":1,\"sourceSessionId\":3}", "InProgress", "2026-11-02 09:02:00"),
                Row(6, ActivitySessionTypes.NativeAttemptReceipt, "[\"k\",\"t3\"]", "{\"version\":1,\"sourceSessionId\":99}", "InProgress", "2026-11-02 09:03:00"),
                Row(7, "VocabularyReview", abandonedKey, bareSnapshot, "Abandoned", "2026-10-30 08:00:00"),
            })
                // Braces in the JSON are literal, not format placeholders.
                await previous.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{").Replace("}", "}}"));
        }

        var webRunId = SentenceStudio.Services.SyncIds.ActivitySession(owner, "VocabularyReview", webKey);
        var nativeRunId = SentenceStudio.Services.SyncIds.ActivitySession(owner, "VocabularyReview", nativeKey);
        await using (var upgraded = CreateContext(scratch.ConnectionString))
        {
            await upgraded.Database.MigrateAsync();
            Assert.False(upgraded.Database.HasPendingModelChanges());
            var rows = await upgraded.ActivitySessions.AsNoTracking().ToListAsync();
            Assert.Equal(7, rows.Count);

            var webRun = rows.Single(row => row.Id == webRunId);
            Assert.Equal(ActivitySessionStatus.InProgress, webRun.Status);
            var wrapped = VocabQuizSavedState.Deserialize(webRun.StateJson);
            Assert.Equal((owner, webKey, new DateOnly(2026, 11, 2), "p1", 3, ""),
                (wrapped.UserId, wrapped.LaunchContextKey, wrapped.PlanDate,
                    wrapped.Snapshot.PlanItemId, wrapped.Snapshot.TotalTurns, wrapped.DisplayDirection));
            Assert.True(Guid.TryParseExact(wrapped.TurnId, "N", out _));
            Assert.Null(wrapped.SavedBy);
            var olderRun = rows.Single(row => row.LaunchContextKey == webKey && row.Status == ActivitySessionStatus.Completed);
            Assert.True(Guid.TryParse(olderRun.Id, out _));

            Assert.Equal(nativeState, rows.Single(row => row.Id == nativeRunId).StateJson);
            Assert.Equal(SentenceStudio.Services.SyncIds.ActivitySession(owner, "VocabularyReview", abandonedKey),
                rows.Single(row => row.LaunchContextKey == abandonedKey).Id);

            var receipts = rows.Where(row => ActivitySessionTypes.IsDeviceOnly(row.ActivityType, row.Id))
                .OrderBy(row => row.UpdatedAt).ToList();
            Assert.Equal(3, receipts.Count);
            Assert.All(receipts, row => Assert.StartsWith(ActivitySessionTypes.ReceiptIdPrefix, row.Id));
            string Source(ActivitySession row) => System.Text.Json.JsonDocument.Parse(row.StateJson)
                .RootElement.GetProperty("sourceSessionId").GetString()!;
            Assert.Equal([nativeRunId, nativeRunId, "99"], receipts.Select(Source));

            // One in-progress run per learner + activity + launch is still enforced.
            upgraded.ActivitySessions.Add(new ActivitySession
            {
                Id = Guid.NewGuid().ToString(), UserId = owner, ActivityType = "VocabularyReview",
                LaunchContextKey = webKey, StateJson = "{}", Status = ActivitySessionStatus.InProgress,
                StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => upgraded.SaveChangesAsync());
        }

        await using (var downgraded = CreateContext(scratch.ConnectionString))
            await downgraded.GetService<IMigrator>().MigrateAsync(Phase2aMigration);
        await using var connection = new SqliteConnection(scratch.ConnectionString);
        await connection.OpenAsync();
        async Task<string> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        }
        Assert.Equal("7|7", await Scalar(
            "SELECT count(*) || '|' || sum(typeof(\"Id\") = 'integer') FROM \"ActivitySession\""));
        var nativeNumber = await Scalar(
            $"SELECT \"Id\" FROM \"ActivitySession\" WHERE \"LaunchContextKey\" = '{nativeKey}'");
        Assert.Equal($"{nativeNumber}|{nativeNumber}|99", await Scalar(
            "SELECT group_concat(json_extract(\"StateJson\", '$.sourceSessionId'), '|') FROM "
            + "(SELECT \"StateJson\" FROM \"ActivitySession\" WHERE \"ActivityType\" LIKE '%Receipt%' ORDER BY \"UpdatedAt\")"));
        Assert.Equal("p1|", await Scalar(
            $"SELECT json_extract(\"StateJson\", '$.planItemId') || '|' || ifnull(json_extract(\"StateJson\", '$.snapshot'), '') "
            + $"FROM \"ActivitySession\" WHERE \"LaunchContextKey\" = '{webKey}' AND \"Status\" = 'InProgress'"));
        Assert.Equal(nativeState, await Scalar(
            $"SELECT \"StateJson\" FROM \"ActivitySession\" WHERE \"LaunchContextKey\" = '{nativeKey}'"));
    }

    /// <summary>
    /// Phase 3 (docs/sync-all-tables-plan.md): scene images, scenarios and example sentences get
    /// text IDs and owners. Rebuilding "Conversation" must keep every conversation's chunks and
    /// memory (they cascade from it), its links follow their scenario, built-ins get their fixed
    /// IDs (a second copy does not), and Down restores numbers, links and the constraint.
    /// </summary>
    [Fact]
    public async Task SyncOwnersPhase3_GivesTextIdsAndOwners_KeepsConversationsAndTheirChunks_AndDownRestoresThem()
    {
        using var scratch = new ScratchDatabase();
        const string owner = "owner-a";
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(Phase2bMigration);
            foreach (var sql in new[]
            {
                "INSERT INTO \"UserProfile\" (\"Id\", \"Name\", \"NativeLanguage\", \"TargetLanguage\", \"PreferredSessionMinutes\", \"CreatedAt\") VALUES ('owner-a', 'A', 'English', 'Korean', 20, '2026-11-01 00:00:00')",
                "INSERT INTO \"LearningResource\" (\"Id\", \"Title\", \"UserProfileId\", \"IsSmartResource\", \"CreatedAt\", \"UpdatedAt\") VALUES ('res-a', 'R', 'owner-a', 0, '2026-11-01 00:00:00', '2026-11-01 00:00:00')",
                "INSERT INTO \"VocabularyWord\" (\"Id\", \"TargetLanguageTerm\", \"UserProfileId\", \"CreatedAt\", \"UpdatedAt\") VALUES ('word-owned', '물', 'owner-a', '2026-11-01 00:00:00', '2026-11-01 00:00:00')",
                "INSERT INTO \"VocabularyWord\" (\"Id\", \"TargetLanguageTerm\", \"CreatedAt\", \"UpdatedAt\") VALUES ('word-shared', '불', '2026-11-01 00:00:00', '2026-11-01 00:00:00')",
                ScenarioRow(1, "First Meeting", predefined: true, "2026-11-01 01:00:00"),
                ScenarioRow(2, "Ordering Coffee", predefined: true, "2026-11-01 01:00:01"),
                ScenarioRow(3, "First Meeting", predefined: true, "2026-11-01 01:00:02"),
                ScenarioRow(4, "My Job Interview", predefined: false, "2026-11-01 01:00:03"),
                ConversationRow("conv-builtin", 2),
                ConversationRow("conv-custom", 4),
                ConversationRow("conv-none", null),
                "INSERT INTO \"ConversationChunk\" (\"Id\", \"ConversationId\", \"SentTime\", \"Author\", \"Text\", \"Role\", \"Comprehension\", \"UserProfileId\") VALUES ('chunk-1', 'conv-builtin', '2026-11-01 02:00:00', 'me', '안녕', 0, 0, 'owner-a')",
                "INSERT INTO \"ConversationChunk\" (\"Id\", \"ConversationId\", \"SentTime\", \"Author\", \"Text\", \"Role\", \"Comprehension\", \"UserProfileId\") VALUES ('chunk-2', 'conv-custom', '2026-11-01 02:00:01', 'me', '네', 0, 0, 'owner-a')",
                "INSERT INTO \"SceneImage\" (\"Id\", \"Url\", \"Description\", \"IsSelected\") VALUES (1, 'https://example.test/a.jpg', 'A', 0)",
                "INSERT INTO \"SceneImage\" (\"Id\", \"Url\", \"Description\", \"IsSelected\") VALUES (2, 'native-scene-image:b.jpg', 'B', 0)",
                SentenceRow(1, "word-owned", null, "물을 마셔요."),
                SentenceRow(2, "word-shared", "res-a", "불이 뜨거워요."),
                SentenceRow(3, "word-shared", null, "불을 꺼요."),
            })
                await previous.Database.ExecuteSqlRawAsync(sql);
        }

        await using (var upgraded = CreateContext(scratch.ConnectionString))
        {
            await upgraded.Database.MigrateAsync();
            Assert.False(upgraded.Database.HasPendingModelChanges());

            var scenarios = await upgraded.ConversationScenarios.AsNoTracking().ToListAsync();
            Assert.Equal(4, scenarios.Count);
            var firstMeeting = SentenceStudio.Services.SyncIds.BuiltInScenario("First Meeting");
            var coffee = SentenceStudio.Services.SyncIds.BuiltInScenario("Ordering Coffee");
            Assert.Equal("scn:first-meeting", firstMeeting);
            Assert.Single(scenarios, row => row.Id == firstMeeting);
            Assert.Single(scenarios, row => row.Id == coffee);
            // The second copy of a built-in keeps its row under a GUID.
            Assert.Equal(2, scenarios.Count(row => row.Name == "First Meeting"));
            var custom = scenarios.Single(row => !row.IsPredefined);
            Assert.True(Guid.TryParse(custom.Id, out _));
            Assert.All(scenarios, row => Assert.Null(row.UserProfileId));

            var conversations = await upgraded.Conversations.AsNoTracking().ToDictionaryAsync(row => row.Id);
            Assert.Equal(3, conversations.Count);
            Assert.Equal(coffee, conversations["conv-builtin"].ScenarioId);
            Assert.Equal(custom.Id, conversations["conv-custom"].ScenarioId);
            Assert.Null(conversations["conv-none"].ScenarioId);
            // The rebuild deleted no chunk (they cascade from their conversation).
            Assert.Equal(["chunk-1", "chunk-2"], (await upgraded.ConversationChunks.AsNoTracking()
                .Select(row => row.Id).OrderBy(id => id).ToListAsync()));

            var images = await upgraded.SceneImages.AsNoTracking().OrderBy(row => row.Url).ToListAsync();
            Assert.Equal(2, images.Count);
            Assert.All(images, row => Assert.True(Guid.TryParse(row.Id, out _)));
            Assert.All(images, row => Assert.Null(row.UserProfileId));

            var sentences = await upgraded.ExampleSentences.AsNoTracking().ToDictionaryAsync(row => row.TargetSentence);
            Assert.Equal(3, sentences.Count);
            Assert.All(sentences.Values, row => Assert.True(Guid.TryParse(row.Id, out _)));
            Assert.Equal(owner, sentences["물을 마셔요."].UserProfileId);
            Assert.Equal(owner, sentences["불이 뜨거워요."].UserProfileId);
            Assert.Null(sentences["불을 꺼요."].UserProfileId);
        }

        await using (var downgraded = CreateContext(scratch.ConnectionString))
            await downgraded.GetService<IMigrator>().MigrateAsync(Phase2bMigration);
        await using var connection = new SqliteConnection(scratch.ConnectionString);
        await connection.OpenAsync();
        async Task<string> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        }
        foreach (var table in new[] { "SceneImage", "ConversationScenario", "ExampleSentence" })
            Assert.Equal(await Scalar($"SELECT count(*) FROM \"{table}\""),
                await Scalar($"SELECT sum(typeof(\"Id\") = 'integer') FROM \"{table}\""));
        Assert.Equal("Ordering Coffee", await Scalar(
            "SELECT s.\"Name\" FROM \"Conversation\" c JOIN \"ConversationScenario\" s ON s.\"Id\" = c.\"ScenarioId\" WHERE c.\"Id\" = 'conv-builtin'"));
        Assert.Equal("My Job Interview", await Scalar(
            "SELECT s.\"Name\" FROM \"Conversation\" c JOIN \"ConversationScenario\" s ON s.\"Id\" = c.\"ScenarioId\" WHERE c.\"Id\" = 'conv-custom'"));
        Assert.Equal("2", await Scalar("SELECT count(*) FROM \"ConversationChunk\""));
        Assert.Equal("1", await Scalar(
            "SELECT count(*) FROM pragma_foreign_key_list('Conversation') WHERE \"table\" = 'ConversationScenario'"));
        Assert.Equal("", await Scalar("SELECT group_concat(\"table\") FROM pragma_foreign_key_check"));

        static string ScenarioRow(int id, string name, bool predefined, string at) =>
            "INSERT INTO \"ConversationScenario\" (\"Id\", \"Name\", \"PersonaName\", \"PersonaDescription\", \"SituationDescription\", \"ConversationType\", \"IsPredefined\", \"CreatedAt\", \"UpdatedAt\") "
            + $"VALUES ({id}, '{name}', 'P', 'D', 'S', 0, {(predefined ? 1 : 0)}, '{at}', '{at}')";
        static string ConversationRow(string id, int? scenario) =>
            "INSERT INTO \"Conversation\" (\"Id\", \"CreatedAt\", \"Language\", \"ScenarioId\", \"UserProfileId\") "
            + $"VALUES ('{id}', '2026-11-01 02:00:00', 'Korean', {(scenario is null ? "NULL" : scenario.ToString())}, 'owner-a')";
        static string SentenceRow(int id, string word, string? resource, string text) =>
            "INSERT INTO \"ExampleSentence\" (\"Id\", \"VocabularyWordId\", \"LearningResourceId\", \"TargetSentence\", \"IsCore\", \"CreatedAt\", \"UpdatedAt\") "
            + $"VALUES ({id}, '{word}', {(resource is null ? "NULL" : $"'{resource}'")}, '{text}', 0, '2026-11-01 03:0{id}:00', '2026-11-01 03:0{id}:00')";
    }

    /// <summary>
    /// Lookup history (Captain 2026-11-04, decision 4 B): a new owner-scoped table. Up keeps every
    /// existing row, a lookup round-trips its UTC times and direction text, and Down drops only
    /// the new table.
    /// </summary>
    [Fact]
    public async Task AddVocabularyLookup_CreatesTheTable_KeepsExistingRows_AndDownDropsOnlyIt()
    {
        using var scratch = new ScratchDatabase();
        await using (var previous = CreateContext(scratch.ConnectionString))
        {
            await previous.Database.MigrateAsync(Phase3Migration);
            await previous.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"UserProfile\" (\"Id\", \"Name\", \"NativeLanguage\", \"TargetLanguage\", \"PreferredSessionMinutes\", \"CreatedAt\") VALUES ('owner-a', 'A', 'English', 'Korean', 20, '2026-11-01 00:00:00')");
            await previous.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"UserActivity\" (\"Id\", \"Activity\", \"Input\", \"Accuracy\", \"Fluency\", \"UserProfileId\", \"CreatedAt\", \"UpdatedAt\") VALUES ('act-1', 'Reading', 'Dictionary lookup: 물', 100, 100, 'owner-a', '2026-11-01 00:00:00', '2026-11-01 00:00:00')");
        }

        var first = new DateTime(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc);
        var id = SentenceStudio.Services.SyncIds.VocabularyLookup(
            "owner-a", "Korean", VocabularyLookupDirection.ToNative, "물");
        await using (var upgraded = CreateContext(scratch.ConnectionString))
        {
            await upgraded.Database.MigrateAsync();
            Assert.Equal(LookupMigration, (await upgraded.Database.GetAppliedMigrationsAsync()).Last());
            Assert.False(upgraded.Database.HasPendingModelChanges());
            Assert.Equal(1, await upgraded.UserActivities.CountAsync());
            upgraded.VocabularyLookups.Add(new VocabularyLookup
            {
                Id = id, UserProfileId = "owner-a", Text = "물", Translation = "water",
                Language = "Korean", Direction = VocabularyLookupDirection.ToNative,
                Activity = VocabularyLookupActivity.Reading, ResourceId = null,
                FirstLookedUpAt = first, LastLookedUpAt = first.AddMinutes(5)
            });
            await upgraded.SaveChangesAsync();
        }

        await using (var reread = CreateContext(scratch.ConnectionString))
        {
            var row = await reread.VocabularyLookups.AsNoTracking().SingleAsync();
            Assert.Equal(first, row.FirstLookedUpAt);
            Assert.Equal(DateTimeKind.Utc, row.LastLookedUpAt.Kind);
            Assert.Equal("물", row.TargetTerm);
            Assert.Equal("water", row.NativeTerm);
        }

        await using var connection = new SqliteConnection(scratch.ConnectionString);
        await connection.OpenAsync();
        async Task<string> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        }
        Assert.Equal("ToNative", await Scalar("SELECT \"Direction\" FROM \"VocabularyLookup\""));
        Assert.Equal("1", await Scalar(
            "SELECT count(*) FROM pragma_index_list('VocabularyLookup') WHERE name = 'IX_VocabularyLookup_UserProfileId_LastLookedUpAt'"));
        Assert.Equal("0", await Scalar(
            "SELECT count(*) FROM pragma_table_info('VocabularyLookup') WHERE name = 'ResourceId' AND \"notnull\" = 1"));
        Assert.Equal("", await Scalar("SELECT group_concat(\"table\") FROM pragma_foreign_key_list('VocabularyLookup')"));

        await using (var downgraded = CreateContext(scratch.ConnectionString))
            await downgraded.GetService<IMigrator>().MigrateAsync(Phase3Migration);
        Assert.Equal("0", await Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'VocabularyLookup'"));
        Assert.Equal("1", await Scalar("SELECT count(*) FROM \"UserActivity\""));
        Assert.Equal(Phase3Migration, await Scalar(
            "SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\""));
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
            word.Ignore(row => row.UserProfileId);
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
