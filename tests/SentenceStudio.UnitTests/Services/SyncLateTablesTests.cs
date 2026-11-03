using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Phase 1 of syncing every learner table (Captain 2026-11-02, docs/sync-all-tables-plan.md):
/// rows written before a table joined sync move only after a one-time touch, only for the
/// signed-in owner; Number Drill rows get the same ID on every store.
/// </summary>
public sealed class SyncLateTablesTests
{
    private const string Owner = "owner-a";
    private const string Other = "owner-b";

    [Fact]
    public async Task RowsWrittenBeforeTracking_SyncOnlyAfterTouch_AndOnlyTheOwners()
    {
        var local = await CreateStoreAsync(ProviderMode.Local, provision: true);
        var remote = await CreateStoreAsync(ProviderMode.Remote, provision: true);
        await using var localKeepAlive = local.KeepAlive;
        await using var remoteKeepAlive = remote.KeepAlive;

        // A device that has synced before: CoreSync sends a full snapshot only on a first sync.
        await new SyncAgent(local.Provider, remote.Provider).SynchronizeAsync();
        // The previous app version did not track these tables.
        await DropLateTriggersAsync(local.KeepAlive);

        await using (var db = new ApplicationDbContext(local.Options))
        {
            // Parents of the Minimal Pairs rows (already-synced tables, so they upload anyway).
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = "w1", TargetLanguageTerm = "w1" },
                new VocabularyWord { Id = "w2", TargetLanguageTerm = "w2" });
            await db.SaveChangesAsync();
            var pairId = SyncIds.MinimalPair(Owner, "w1", "w2");
            db.MinimalPairs.Add(new MinimalPair
            {
                Id = pairId, UserId = Owner, VocabularyWordAId = "w1", VocabularyWordBId = "w2"
            });
            db.MinimalPairSessions.Add(new MinimalPairSession { Id = "session-a", UserId = Owner });
            db.MinimalPairAttempts.Add(new MinimalPairAttempt
            {
                Id = "pair-attempt-a", UserId = Owner, SessionId = "session-a", PairId = pairId,
                PromptWordId = "w1", SelectedWordId = "w2"
            });
            db.StreamHistories.AddRange(
                new StreamHistory { Id = "stream-a", UserProfileId = Owner },
                new StreamHistory { Id = "stream-b", UserProfileId = Other },
                new StreamHistory { Id = "stream-unowned", UserProfileId = null });
            db.NumberAttempts.AddRange(
                new NumberAttempt { Id = "attempt-a", UserProfileId = Owner },
                new NumberAttempt { Id = "attempt-b", UserProfileId = Other });
            db.NumberMasteryProgresses.Add(new NumberMasteryProgress
            {
                Id = "mastery-a", UserProfileId = Owner, LanguageCode = "ko",
                ContextCode = "age", System = NumberSystem.Native, Bucket = "1-10"
            });
            db.WordAssociationScores.Add(new WordAssociationScore { Id = "score-a", UserProfileId = Owner });
            await db.SaveChangesAsync();
        }

        IReadOnlyList<string> untracked;
        await using (var db = new ApplicationDbContext(local.Options))
            untracked = await SyncLateTables.UntrackedAsync(db);
        Assert.Equal(SyncLateTables.OwnerColumns.Keys, untracked);

        await local.Provider.ApplyProvisionAsync();
        await SyncLikeTheAppAsync(local, remote);
        Assert.Empty(await RemoteIdsAsync(remote));

        await using (var db = new ApplicationDbContext(local.Options))
        {
            Assert.Empty(await SyncLateTables.UntrackedAsync(db));
            Assert.Equal(7, await SyncLateTables.TouchAsync(
                db, untracked, Owner, NullLogger.Instance));
        }
        await SyncLikeTheAppAsync(local, remote);

        Assert.Equal(["attempt-a", "mastery-a", "mp:owner-a:w1:w2", "pair-attempt-a", "score-a",
            "session-a", "stream-a"], await RemoteIdsAsync(remote));
    }

    [Fact]
    public async Task ServerRowsWrittenBeforeTracking_ReachAnExistingDeviceAfterTheServerTouch()
    {
        var device = await CreateStoreAsync(ProviderMode.Local, provision: true);
        var server = await CreateStoreAsync(ProviderMode.Remote, provision: true);
        await using var deviceKeepAlive = device.KeepAlive;
        await using var serverKeepAlive = server.KeepAlive;
        await SyncLikeTheAppAsync(device, server);
        await DropLateTriggersAsync(server.KeepAlive);

        // Written by the web app before these tables joined sync.
        await using (var db = new ApplicationDbContext(server.Options))
        {
            db.NumberAttempts.Add(new NumberAttempt { Id = "web-attempt", UserProfileId = Owner });
            db.WordAssociationScores.Add(new WordAssociationScore { Id = "web-score", UserProfileId = Owner });
            await db.SaveChangesAsync();
        }

        IReadOnlyList<string> untracked;
        await using (var db = new ApplicationDbContext(server.Options))
            untracked = await SyncLateTables.UntrackedAsync(db);
        await server.Provider.ApplyProvisionAsync();
        await SyncLikeTheAppAsync(device, server);
        Assert.Empty(await RemoteIdsAsync(device));

        await using (var db = new ApplicationDbContext(server.Options))
            await SyncLateTables.TouchAsync(db, untracked, owner: null, NullLogger.Instance);
        await SyncLikeTheAppAsync(device, server);

        Assert.Equal(["web-attempt", "web-score"], await RemoteIdsAsync(device));
    }

    [Fact]
    public async Task Touch_RefusesATableThatIsNotLateJoined()
    {
        var store = await CreateStoreAsync(ProviderMode.Local, provision: true);
        await using var keepAlive = store.KeepAlive;
        await using var db = new ApplicationDbContext(store.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SyncLateTables.TouchAsync(db, ["DiaryEntry"], Owner, NullLogger.Instance));
    }

    [Fact]
    public void Ids_AreStableVersion5GuidsThatDifferPerItem()
    {
        var counter = NumberSyncIdentity.CounterId("ko", "개");
        Assert.Equal(counter, NumberSyncIdentity.CounterId("ko", "개"));
        Assert.NotEqual(counter, NumberSyncIdentity.CounterId("ko", "명"));
        Assert.Equal('5', Guid.Parse(counter).ToString()[14]);
        Assert.NotEqual(NumberSyncIdentity.ContextId("age"), NumberSyncIdentity.SubModeId("age"));

        var mastery = NumberSyncIdentity.MasteryId(Owner, "ko", "age", counter, NumberSystem.Native, "1-10");
        Assert.Equal(mastery,
            NumberSyncIdentity.MasteryId(Owner, "ko", "age", counter, NumberSystem.Native, "1-10"));
        Assert.NotEqual(mastery,
            NumberSyncIdentity.MasteryId(Other, "ko", "age", counter, NumberSystem.Native, "1-10"));
        Assert.NotEqual(mastery,
            NumberSyncIdentity.MasteryId(Owner, "ko", "age", null, NumberSystem.Native, "1-10"));
        Assert.Throws<ArgumentException>(() =>
            NumberSyncIdentity.MasteryId("", "ko", "age", null, NumberSystem.Native, "1-10"));
    }

    [Fact]
    public async Task Normalize_MovesOldRandomIdsToFixedIds_KeepsData_AndIsIdempotent()
    {
        var store = await CreateStoreAsync(ProviderMode.Local, provision: false);
        await using var keepAlive = store.KeepAlive;
        await using (var db = new ApplicationDbContext(store.Options))
        {
            db.NumberContexts.Add(new NumberContext { Id = "old-context", Code = "age" });
            db.NumberSubModes.Add(new NumberSubMode { Id = "old-submode", Code = "produce" });
            db.NumberCounters.Add(new NumberCounter { Id = "old-counter", LanguageCode = "ko", Counter = "살" });
            db.NumberAttempts.Add(new NumberAttempt
            {
                Id = "attempt", UserProfileId = Owner, CounterId = "살", ContextCode = "age"
            });
            db.NumberMasteryProgresses.AddRange(
                new NumberMasteryProgress
                {
                    Id = "old-mastery", UserProfileId = Owner, LanguageCode = "ko", ContextCode = "age",
                    CounterId = "살", System = NumberSystem.Native, Bucket = "1-10",
                    CorrectCount = 4, TotalCount = 5
                },
                new NumberMasteryProgress
                {
                    Id = "unowned-mastery", UserProfileId = "", LanguageCode = "ko",
                    ContextCode = "age", System = NumberSystem.Sino, Bucket = "1-10"
                });
            await db.SaveChangesAsync();
        }

        NumberIdentityReport first;
        await using (var db = new ApplicationDbContext(store.Options))
            first = await NumberSyncIdentity.NormalizeAsync(db, NullLogger.Instance);
        Assert.Equal(new NumberIdentityReport(1, 1, 1, 1, 0), first);

        var counter = NumberSyncIdentity.CounterId("ko", "살");
        // Attempts and mastery hold the counter word, which is the same on every store.
        var masteryId = NumberSyncIdentity.MasteryId(Owner, "ko", "age", "살", NumberSystem.Native, "1-10");
        await using (var db = new ApplicationDbContext(store.Options))
        {
            Assert.Equal(NumberSyncIdentity.ContextId("age"), (await db.NumberContexts.SingleAsync()).Id);
            Assert.Equal(NumberSyncIdentity.SubModeId("produce"), (await db.NumberSubModes.SingleAsync()).Id);
            Assert.Equal(counter, (await db.NumberCounters.SingleAsync()).Id);
            Assert.Equal("살", (await db.NumberAttempts.SingleAsync()).CounterId);
            var moved = await db.NumberMasteryProgresses.SingleAsync(row => row.UserProfileId == Owner);
            Assert.Equal(masteryId, moved.Id);
            Assert.Equal("살", moved.CounterId);
            Assert.Equal((4, 5), (moved.CorrectCount, moved.TotalCount));
            // A row without an owner is never given one or moved.
            Assert.True(await db.NumberMasteryProgresses.AnyAsync(row => row.Id == "unowned-mastery"));
        }

        await using (var db = new ApplicationDbContext(store.Options))
            Assert.False((await NumberSyncIdentity.NormalizeAsync(db, NullLogger.Instance)).Changed);
    }

    [Fact]
    public async Task Normalize_KeepsADuplicateBucketRowInsteadOfOverwritingIt()
    {
        var store = await CreateStoreAsync(ProviderMode.Local, provision: false);
        await using var keepAlive = store.KeepAlive;
        var fixedId = NumberSyncIdentity.MasteryId(Owner, "ko", "money", null, NumberSystem.Sino, "1-10");
        await using (var db = new ApplicationDbContext(store.Options))
        {
            // Unique indexes treat a missing counter as distinct, so two rows can share a bucket.
            db.NumberMasteryProgresses.AddRange(
                new NumberMasteryProgress
                {
                    Id = fixedId, UserProfileId = Owner, LanguageCode = "ko", ContextCode = "money",
                    System = NumberSystem.Sino, Bucket = "1-10", TotalCount = 7
                },
                new NumberMasteryProgress
                {
                    Id = "duplicate", UserProfileId = Owner, LanguageCode = "ko", ContextCode = "money",
                    System = NumberSystem.Sino, Bucket = "1-10", TotalCount = 2
                });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(store.Options))
        {
            var report = await NumberSyncIdentity.NormalizeAsync(db, NullLogger.Instance);
            Assert.Equal(1, report.MasteryKept);
            Assert.False(report.Changed);
        }
        await using (var db = new ApplicationDbContext(store.Options))
        {
            Assert.Equal(7, (await db.NumberMasteryProgresses.SingleAsync(row => row.Id == fixedId)).TotalCount);
            Assert.Equal(2, (await db.NumberMasteryProgresses.SingleAsync(row => row.Id == "duplicate")).TotalCount);
        }
    }

    [Fact]
    public async Task Seeder_CreatesCatalogRowsWithFixedIds()
    {
        var store = await CreateStoreAsync(ProviderMode.Local, provision: false);
        await using var keepAlive = store.KeepAlive;
        await using var db = new ApplicationDbContext(store.Options);
        await new NumberContentSeeder(db, NullLogger<NumberContentSeeder>.Instance).SeedAsync("ko");

        var contexts = await db.NumberContexts.AsNoTracking().ToListAsync();
        var counters = await db.NumberCounters.AsNoTracking().ToListAsync();
        Assert.NotEmpty(contexts);
        Assert.NotEmpty(counters);
        Assert.All(contexts, row => Assert.Equal(NumberSyncIdentity.ContextId(row.Code), row.Id));
        Assert.All(await db.NumberSubModes.AsNoTracking().ToListAsync(),
            row => Assert.Equal(NumberSyncIdentity.SubModeId(row.Code), row.Id));
        Assert.All(counters,
            row => Assert.Equal(NumberSyncIdentity.CounterId(row.LanguageCode, row.Counter), row.Id));
    }

    /// <summary>
    /// The app's rules: SyncService skips duplicate inserts and force-writes updates, and the
    /// CoreSync HTTP server applies uploaded updates with ForceWrite, so a touched row the
    /// receiver lacks is inserted.
    /// </summary>
    private static Task SyncLikeTheAppAsync(Store local, Store remote) =>
        new SyncAgent(local.Provider, remote.Provider).SynchronizeAsync(
            remoteConflictResolutionFunc: Rule, localConflictResolutionFunc: Rule);

    private static ConflictResolution Rule(SyncItem item) =>
        item.ChangeType == ChangeType.Insert ? ConflictResolution.Skip : ConflictResolution.ForceWrite;

    private static async Task DropLateTriggersAsync(SqliteConnection connection)
    {
        foreach (var table in SyncLateTables.OwnerColumns.Keys)
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP TRIGGER [__{table}_ct-{op}__]";
                await drop.ExecuteNonQueryAsync();
            }
    }

    private static async Task<List<string>> RemoteIdsAsync(Store remote)
    {
        await using var db = new ApplicationDbContext(remote.Options);
        return (await db.NumberAttempts.Select(row => row.Id).ToListAsync())
            .Concat(await db.NumberMasteryProgresses.Select(row => row.Id).ToListAsync())
            .Concat(await db.WordAssociationScores.Select(row => row.Id).ToListAsync())
            .Concat(await db.MinimalPairs.Select(row => row.Id).ToListAsync())
            .Concat(await db.MinimalPairSessions.Select(row => row.Id).ToListAsync())
            .Concat(await db.MinimalPairAttempts.Select(row => row.Id).ToListAsync())
            .Concat(await db.StreamHistories.Select(row => row.Id).ToListAsync())
            .Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<Store> CreateStoreAsync(ProviderMode mode, bool provision)
    {
        var connectionString = $"Data Source=LateTables{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(keepAlive).Options;
        await using (var db = new ApplicationDbContext(options))
            await db.Database.EnsureCreatedAsync();
        var provider = new SqliteSyncProvider(
            new SqliteSyncConfigurationBuilder(connectionString).ConfigureSyncTables().Build(), mode);
        if (provision)
            await provider.ApplyProvisionAsync();
        return new Store(keepAlive, options, provider);
    }

    private sealed record Store(
        SqliteConnection KeepAlive, DbContextOptions<ApplicationDbContext> Options, ISyncProvider Provider);
}
