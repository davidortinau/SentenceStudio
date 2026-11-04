using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.SqliteMigrations.Tests;

/// <summary>
/// An installed MAUI app updated to this branch (Captain 2026-11-04: the MAUI apps keep shipping).
/// Its database is at the last migration on main, holds every learner's rows from headerless
/// sync, rows with no owner and a legacy table, and has CoreSync tracking for main's tables. It
/// must migrate to the branch head without losing a row, then build an owner-checked upload
/// with the MAUI upload rules (the server side of that sync is proven in
/// SentenceStudio.Api.Tests MauiOwnerCheckedSyncPostgresTests).
/// </summary>
public sealed class MauiUpgradeFromMainTests
{
    /// <summary>The newest SQLite migration on main (git show main:src/SentenceStudio.Shared/Migrations/Sqlite).</summary>
    private const string MainMigration = "20260903175044_AddApplicationOperationLedger";
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Other = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task MainDatabase_MigratesWithEveryRow_ThenUploadsOnlyTheLearnersRows()
    {
        var directory = Directory.CreateTempSubdirectory("SentenceStudio.MauiUpgrade.");
        try
        {
            var path = Path.Combine(directory.FullName, "sstudio.db3");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path, Pooling = false
            }.ToString();

            await using (var main = CreateContext(connectionString))
                await main.Database.MigrateAsync(MainMigration);
            await ProvisionMainSyncAsync(connectionString);
            await SeedMainEraRowsAsync(connectionString);
            var before = await CountRowsAsync(connectionString);

            // The app's upgrade path: verified copy first, then every pending migration.
            await using (var upgraded = CreateContext(connectionString))
            {
                var backup = await SqliteMigrationBackup.MigrateWithBackupAsync(
                    upgraded, path, NullLogger.Instance);
                Assert.NotNull(backup);
                Assert.True(File.Exists(backup));
                Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
                Assert.False(upgraded.Database.HasPendingModelChanges());
            }
            Assert.Equal(before, await CountRowsAsync(connectionString));
            await AssertConvertedRowsAsync(connectionString);

            // Next sync: CoreSync tracks the head's tables, and the MAUI upload rules choose rows.
            var local = new SqliteSyncProvider(
                new SqliteSyncConfigurationBuilder(connectionString).ConfigureSyncTables().Build(),
                ProviderMode.Local);
            await local.ApplyProvisionAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString).Options;
            var upload = await new OwnerCheckedSyncProvider(
                    local, () => new ApplicationDbContext(options), new FixedOwner(Owner),
                    NullLogger<OwnerCheckedSyncProvider>.Instance)
                .GetChangesAsync(Guid.NewGuid(), syncDirection: SyncDirection.UploadOnly);
            var sent = upload.Items.Select(item => (item.TableName, Id: item.Values["Id"].Value?.ToString()))
                .ToHashSet();

            Assert.Contains(("UserProfile", Owner), sent);
            Assert.Contains(("LearningResource", "res-owner"), sent);
            Assert.Contains(("VocabularyWord", "word-owner-1"), sent);
            Assert.Contains(("VocabularyProgress", "progress-owner"), sent);
            Assert.Contains(("MinimalPair", SyncIds.MinimalPair(Owner, "word-owner-1", "word-owner-2")), sent);
            Assert.Contains(("Conversation", "conv-builtin"), sent);
            Assert.Contains(("ConversationChunk", "chunk-1"), sent);
            // Another learner's rows and the device-only rows stay on the device.
            Assert.DoesNotContain(sent, row => row.Id is Other or "res-other" or "progress-other"
                or "word-other" or "map-other");
            Assert.DoesNotContain(sent, row => row.TableName is "SceneImage" or "ConversationScenario");
            // Of the two examples, only the one that gained the learner as owner leaves.
            var example = Assert.Single(upload.Items, item => item.TableName == "ExampleSentence");
            Assert.Equal(Owner, example.Values["UserProfileId"].Value?.ToString());
            // Building the upload deleted and re-owned nothing.
            Assert.Equal(before, await CountRowsAsync(connectionString));
            await AssertConvertedRowsAsync(connectionString);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    /// <summary>CoreSync tracking as main's MAUI build provisioned it (main's 18 tables).</summary>
    private static async Task ProvisionMainSyncAsync(string connectionString)
    {
        var main = new SqliteSyncConfigurationBuilder(connectionString)
            .Table<LearningResource>("LearningResource")
            .Table<VocabularyWord>("VocabularyWord")
            .Table<ResourceVocabularyMapping>("ResourceVocabularyMapping")
            .Table<Challenge>("Challenge")
            .Table<Conversation>("Conversation")
            .Table<ConversationChunk>("ConversationChunk")
            .Table<UserProfile>("UserProfile")
            .Table<SkillProfile>("SkillProfile")
            .Table<VocabularyList>("VocabularyList")
            .Table<VocabularyProgress>("VocabularyProgress")
            .Table<VocabularyLearningContext>("VocabularyLearningContext")
            .Table<MonitoredChannel>("MonitoredChannel")
            .Table<VideoImport>("VideoImport")
            .Table<DailyPlan>("DailyPlan")
            .Table<DailyPlanCompletion>("DailyPlanCompletion")
            .Table<UserActivity>("UserActivity")
            .Table<DiaryEntry>("DiaryEntry")
            .Table<PhraseConstituent>("PhraseConstituent")
            .Build();
        await new SqliteSyncProvider(main, ProviderMode.Local).ApplyProvisionAsync();
    }

    /// <summary>What an installed MAUI app holds at main's schema (number IDs, no owner columns).</summary>
    private static async Task SeedMainEraRowsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        const string at = "2026-10-01 09:00:00";
        async Task Row(string table, Dictionary<string, object?> values) =>
            await InsertAsync(connection, table, values);

        foreach (var (id, email) in new[] { (Owner, "owner@example.test"), (Other, "other@example.test") })
            await Row("UserProfile", new() { ["Id"] = id, ["Email"] = email, ["Name"] = id[..1],
                ["NativeLanguage"] = "English", ["TargetLanguage"] = "Korean", ["CreatedAt"] = at });
        await Row("LearningResource", new() { ["Id"] = "res-owner", ["UserProfileId"] = Owner, ["Title"] = "Mine", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        // Downloaded from another learner before owner checks.
        await Row("LearningResource", new() { ["Id"] = "res-other", ["UserProfileId"] = Other, ["Title"] = "Theirs", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        foreach (var word in new[] { "word-owner-1", "word-owner-2", "word-other", "word-orphan" })
            await Row("VocabularyWord", new() { ["Id"] = word, ["TargetLanguageTerm"] = word, ["NativeLanguageTerm"] = word, ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("ResourceVocabularyMapping", new() { ["Id"] = "map-1", ["ResourceId"] = "res-owner", ["VocabularyWordId"] = "word-owner-1" });
        await Row("ResourceVocabularyMapping", new() { ["Id"] = "map-2", ["ResourceId"] = "res-owner", ["VocabularyWordId"] = "word-owner-2" });
        await Row("ResourceVocabularyMapping", new() { ["Id"] = "map-other", ["ResourceId"] = "res-other", ["VocabularyWordId"] = "word-other" });
        await Row("VocabularyProgress", new() { ["Id"] = "progress-owner", ["UserId"] = Owner, ["VocabularyWordId"] = "word-owner-1", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("VocabularyProgress", new() { ["Id"] = "progress-other", ["UserId"] = Other, ["VocabularyWordId"] = "word-other", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("MinimalPair", new() { ["Id"] = 7, ["UserId"] = Owner, ["VocabularyWordAId"] = "word-owner-1", ["VocabularyWordBId"] = "word-owner-2", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("MinimalPairSession", new() { ["Id"] = 3, ["UserId"] = Owner, ["Mode"] = "Focus", ["StartedAt"] = at, ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("MinimalPairAttempt", new() { ["UserId"] = Owner, ["SessionId"] = 3, ["PairId"] = 7, ["PromptWordId"] = "word-owner-1", ["SelectedWordId"] = "word-owner-2", ["IsCorrect"] = 0, ["SequenceNumber"] = 1, ["CreatedAt"] = at });
        await Row("ConversationScenario", new() { ["Id"] = 1, ["Name"] = "First Meeting", ["PersonaName"] = "P", ["PersonaDescription"] = "D", ["SituationDescription"] = "S", ["IsPredefined"] = 1, ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("ConversationScenario", new() { ["Id"] = 2, ["Name"] = "My Job Interview", ["PersonaName"] = "P", ["PersonaDescription"] = "D", ["SituationDescription"] = "S", ["IsPredefined"] = 0, ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("Conversation", new() { ["Id"] = "conv-builtin", ["UserProfileId"] = Owner, ["ScenarioId"] = 1, ["Language"] = "Korean", ["CreatedAt"] = at });
        await Row("Conversation", new() { ["Id"] = "conv-custom", ["UserProfileId"] = Owner, ["ScenarioId"] = 2, ["Language"] = "Korean", ["CreatedAt"] = at });
        await Row("ConversationChunk", new() { ["Id"] = "chunk-1", ["ConversationId"] = "conv-builtin", ["UserProfileId"] = Owner, ["SentTime"] = at, ["Author"] = "me", ["Text"] = "안녕" });
        await Row("ConversationMemoryState", new() { ["ConversationId"] = "conv-builtin", ["SerializedState"] = "state", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("SceneImage", new() { ["Id"] = 1, ["Url"] = "https://example.test/a.jpg", ["Description"] = "A" });
        await Row("SceneImage", new() { ["Id"] = 2, ["Url"] = SceneImage.DevicePhotoScheme + "b.jpg", ["Description"] = "B" });
        await Row("ExampleSentence", new() { ["Id"] = 1, ["VocabularyWordId"] = "word-owner-1", ["LearningResourceId"] = "res-owner", ["TargetSentence"] = "물", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("ExampleSentence", new() { ["Id"] = 2, ["VocabularyWordId"] = "word-orphan", ["TargetSentence"] = "꽃", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("StreamHistory", new() { ["Phrase"] = "hello", ["AudioFilePath"] = "hello.mp3", ["Duration"] = 1.5, ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("ActivitySession", new() { ["Id"] = 5, ["UserId"] = Owner, ["ActivityType"] = "VocabularyReview", ["LaunchContextKey"] = "plan:1", ["StateJson"] = "{}", ["Status"] = 2, ["StartedAt"] = at, ["UpdatedAt"] = at });
        await Row("DailyPlanCompletion", new() { ["Id"] = "plan-item-1", ["UserProfileId"] = Owner, ["Date"] = "2026-10-01 00:00:00", ["PlanItemId"] = "item-1", ["ActivityType"] = "VocabularyReview", ["CreatedAt"] = at, ["UpdatedAt"] = at });
        await Row("VocabularyList", new() { ["Id"] = "legacy-list", ["Name"] = "Legacy list" });
    }

    private static async Task AssertConvertedRowsAsync(string connectionString)
    {
        await using var db = CreateContext(connectionString);
        // Words keep no recorded owner on the device; the sync rules derive the single learner
        // their links and progress name, and an orphan word has none.
        var words = await db.VocabularyWords.AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.Equal(4, words.Count);
        Assert.All(words.Values, row => Assert.Null(row.UserProfileId));
        var derived = await SyncRowOwners.GetStoredWordOwnersAsync(db, [.. words.Keys], default);
        Assert.Equal(Owner, derived["word-owner-1"]);
        Assert.Equal(Owner, derived["word-owner-2"]);
        Assert.Equal(Other, derived["word-other"]);
        Assert.Null(derived["word-orphan"]);

        var pair = await db.MinimalPairs.AsNoTracking().SingleAsync();
        Assert.Equal(SyncIds.MinimalPair(Owner, "word-owner-1", "word-owner-2"), pair.Id);
        var session = await db.MinimalPairSessions.AsNoTracking().SingleAsync();
        var attempt = await db.MinimalPairAttempts.AsNoTracking().SingleAsync();
        Assert.Equal((session.Id, pair.Id), (attempt.SessionId, attempt.PairId));

        var scenarios = await db.ConversationScenarios.AsNoTracking().ToDictionaryAsync(row => row.Name);
        Assert.Equal(SyncIds.BuiltInScenario("First Meeting"), scenarios["First Meeting"].Id);
        Assert.True(Guid.TryParse(scenarios["My Job Interview"].Id, out _));
        Assert.All(scenarios.Values, row => Assert.Null(row.UserProfileId));
        var conversations = await db.Conversations.AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.Equal(scenarios["First Meeting"].Id, conversations["conv-builtin"].ScenarioId);
        Assert.Equal(scenarios["My Job Interview"].Id, conversations["conv-custom"].ScenarioId);
        Assert.Equal("chunk-1", (await db.ConversationChunks.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(SyncIds.ConversationMemory("conv-builtin"),
            (await db.ConversationMemoryStates.AsNoTracking().SingleAsync()).Id);

        // MAUI keeps rows from before owners unowned and local (D3).
        var images = await db.SceneImages.AsNoTracking().ToListAsync();
        Assert.Equal(2, images.Count);
        Assert.All(images, row => Assert.Null(row.UserProfileId));
        var sentences = await db.ExampleSentences.AsNoTracking().ToDictionaryAsync(row => row.TargetSentence);
        Assert.Equal(Owner, sentences["물"].UserProfileId);
        Assert.Null(sentences["꽃"].UserProfileId);
        var stream = await db.StreamHistories.AsNoTracking().SingleAsync();
        Assert.True(Guid.TryParse(stream.Id, out _));
        Assert.Null(stream.UserProfileId);
        Assert.False(string.IsNullOrEmpty((await db.ActivitySessions.AsNoTracking().SingleAsync()).Id));
        Assert.False((await db.DailyPlanCompletions.AsNoTracking().SingleAsync()).IsArchived);
        Assert.Equal("Legacy list", (await db.VocabularyLists.AsNoTracking().SingleAsync()).Name);
        Assert.Equal(Other, (await db.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == "res-other")).UserProfileId);
    }

    private static readonly string[] SeededTables =
    [
        "UserProfile", "LearningResource", "VocabularyWord", "ResourceVocabularyMapping",
        "VocabularyProgress", "MinimalPair", "MinimalPairSession", "MinimalPairAttempt",
        "ConversationScenario", "Conversation", "ConversationChunk", "ConversationMemoryState",
        "SceneImage", "ExampleSentence", "StreamHistory", "ActivitySession",
        "DailyPlanCompletion", "VocabularyList",
    ];

    private static async Task<Dictionary<string, long>> CountRowsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var counts = new Dictionary<string, long>();
        foreach (var table in SeededTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }
        return counts;
    }

    /// <summary>
    /// Inserts a row into a table as it exists now, giving every other NOT NULL column without a
    /// default a neutral value, so the seed does not restate each old column.
    /// </summary>
    private static async Task InsertAsync(
        SqliteConnection connection, string table, Dictionary<string, object?> values)
    {
        var row = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}')";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1).ToUpperInvariant();
                var required = reader.GetInt64(2) == 1 && reader.IsDBNull(3) && reader.GetInt64(4) == 0;
                if (required && !row.ContainsKey(name))
                    row[name] = type.Contains("INT") ? 0 : type.Contains("REAL") ? 0.0
                        : name.EndsWith("At", StringComparison.Ordinal) || name.Contains("Date") ? "2026-10-01 09:00:00"
                        : string.Empty;
            }
        }
        await using var command = connection.CreateCommand();
        var columns = row.Keys.ToArray();
        command.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) "
            + $"VALUES ({string.Join(", ", columns.Select((_, i) => $"$p{i}"))})";
        for (var i = 0; i < columns.Length; i++)
            command.Parameters.AddWithValue($"$p{i}", row[columns[i]] ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static ApplicationDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options);

    private sealed class FixedOwner(string owner) : ISyncOwnerSource
    {
        public Task<SyncOwnerCredentials?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<SyncOwnerCredentials?>(new SyncOwnerCredentials(owner, "token"));
    }
}
