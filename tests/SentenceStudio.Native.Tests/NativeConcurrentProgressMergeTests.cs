using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Two native devices of one owner, SQLite to SQLite, with the API's upload merge
/// (<see cref="VocabularyProgressConcurrentMerge"/>) on the server side. Words both devices had
/// already practiced share one VocabularyProgress Id; offline attempts on both must all count.
/// </summary>
public sealed class NativeConcurrentProgressMergeTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Resource = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Word = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const string Progress = "ffffffff-ffff-ffff-ffff-ffffffffffff";
    private static readonly DateTime T0 = new(2026, 10, 31, 9, 0, 0);

    [Fact]
    public async Task TwoDevicesOffline_OnExistingProgress_ConvergeWithEveryAttemptOnce()
    {
        await using var server = await Store.CreateAsync(ProviderMode.Remote);
        await SeedServerAsync(server);
        var remote = new MergingServer(server);
        await using var a = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
        await using var b = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
        var deviceA = Native(a, remote);
        var deviceB = Native(b, remote);
        await SyncLikeAppAsync(deviceA, remote);
        await SyncLikeAppAsync(deviceB, remote);

        await RecordAttemptAsync(a, T0.AddMinutes(10), correct: true);
        await RecordAttemptAsync(b, T0.AddMinutes(20), correct: false);
        await SyncLikeAppAsync(deviceA, remote);
        await SyncLikeAppAsync(deviceB, remote);
        await SyncLikeAppAsync(deviceA, remote);

        foreach (var store in new[] { server, a, b })
            await AssertProgressAsync(store, total: 3, correct: 2, contexts: 3);

        // Repeated syncs never count an attempt twice.
        await SyncLikeAppAsync(deviceB, remote);
        await SyncLikeAppAsync(deviceA, remote);
        foreach (var store in new[] { server, a, b })
            await AssertProgressAsync(store, total: 3, correct: 2, contexts: 3);
    }

    [Fact]
    public async Task DownloadConflict_KeepsAttemptRecordedDuringSync_AndUploadsItMerged()
    {
        await using var server = await Store.CreateAsync(ProviderMode.Remote);
        await SeedServerAsync(server);
        var remote = new MergingServer(server);
        await using var a = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
        await using var b = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
        var deviceA = Native(a, remote);
        await SyncLikeAppAsync(deviceA, remote);
        await SyncLikeAppAsync(Native(b, remote), remote);

        // B has two attempts on the server; A records one while its sync is downloading.
        await RecordAttemptAsync(b, T0.AddMinutes(5), correct: true);
        await RecordAttemptAsync(b, T0.AddMinutes(6), correct: true);
        await SyncLikeAppAsync(Native(b, remote), remote);
        var duringSync = new NativeLocalOnlyMappingExportProvider(
            a.Provider, remote, () => Owner,
            NullLogger<NativeLocalOnlyMappingExportProvider>.Instance,
            async (changes, resolve, ct) =>
            {
                await RecordAttemptAsync(a, T0.AddMinutes(10), correct: false);
                return await a.Provider.ApplyChangesAsync(changes, resolve, ct);
            });
        await SyncLikeAppAsync(duringSync, remote);

        // The local attempt was not overwritten by the downloaded row, and it is still pending.
        await AssertProgressAsync(a, total: 2, correct: 1, contexts: 4);
        var pending = await a.Provider.GetChangesAsync(
            await server.Provider.GetStoreIdAsync(), syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(pending.Items, item => item.TableName == "VocabularyProgress");

        await SyncLikeAppAsync(deviceA, remote);
        await SyncLikeAppAsync(Native(b, remote), remote);
        foreach (var store in new[] { server, a, b })
            await AssertProgressAsync(store, total: 4, correct: 3, contexts: 4);
    }

    private static NativeLocalOnlyMappingExportProvider Native(Store store, ISyncProviderBase remote) =>
        new(store.Provider, remote, () => Owner,
            NullLogger<NativeLocalOnlyMappingExportProvider>.Instance);

    // Mirrors SyncService: skip insert conflicts, force updates and deletes.
    private static Task SyncLikeAppAsync(ISyncProvider native, ISyncProviderBase remote) =>
        new SyncAgent(native, remote).SynchronizeAsync(
            remoteConflictResolutionFunc: item => item.ChangeType == ChangeType.Insert
                ? ConflictResolution.Skip : ConflictResolution.ForceWrite,
            localConflictResolutionFunc: item => item.ChangeType == ChangeType.Insert
                ? ConflictResolution.Skip : ConflictResolution.ForceWrite);

    private static async Task SeedServerAsync(Store server)
    {
        await using var db = server.OpenContext();
        db.UserProfiles.Add(new UserProfile { Id = Owner, Email = "owner@example.test" });
        db.LearningResources.Add(new LearningResource
        {
            Id = Resource, UserProfileId = Owner, Title = "Owned resource"
        });
        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = Word, UserProfileId = Owner, TargetLanguageTerm = "반찬", NativeLanguageTerm = "side dish"
        });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(), ResourceId = Resource, VocabularyWordId = Word
        });
        var row = new VocabularyProgress
        {
            Id = Progress, UserId = Owner, VocabularyWordId = Word,
            FirstSeenAt = T0, CreatedAt = T0, LastPracticedAt = T0, UpdatedAt = T0
        };
        var attempt = Attempt(correct: true);
        VocabularyProgressService.ApplyAttemptScoring(row, attempt, T0);
        db.VocabularyProgresses.Add(row);
        db.VocabularyLearningContexts.Add(
            VocabularyProgressService.CreateLearningContext(row.Id, attempt, T0));
        await db.SaveChangesAsync();
    }

    /// <summary>The native writer's transactional attempt: score the row and add its context.</summary>
    private static async Task RecordAttemptAsync(Store store, DateTime at, bool correct)
    {
        await using var db = store.OpenContext();
        var row = await db.VocabularyProgresses.SingleAsync(item => item.Id == Progress);
        var attempt = Attempt(correct);
        VocabularyProgressService.ApplyAttemptScoring(row, attempt, at);
        var context = VocabularyProgressService.CreateLearningContext(row.Id, attempt, at);
        context.CreatedAt = context.UpdatedAt = at;
        db.VocabularyLearningContexts.Add(context);
        await db.SaveChangesAsync();
    }

    private static VocabularyAttempt Attempt(bool correct) => new()
    {
        VocabularyWordId = Word, UserId = Owner, Activity = "VocabularyQuiz",
        InputMode = "MultipleChoice", ContextType = "Isolated", WasCorrect = correct,
        DifficultyWeight = 1, LearningResourceId = Resource, ResponseTimeMs = 900
    };

    private static async Task AssertProgressAsync(Store store, int total, int correct, int contexts)
    {
        await using var db = store.OpenContext();
        var row = await db.VocabularyProgresses.AsNoTracking().SingleAsync();
        Assert.Equal(Progress, row.Id);
        Assert.Equal(total, row.TotalAttempts);
        Assert.Equal(correct, row.CorrectAttempts);
        Assert.Equal(correct, row.QuizRecognitionDemonstrations);
#pragma warning disable CS0618
        Assert.Equal(total, row.RecognitionAttempts);
#pragma warning restore CS0618
        Assert.Equal(contexts, await db.VocabularyLearningContexts.AsNoTracking().CountAsync());
    }

    /// <summary>The API's native upload path: merge under the upload, force it, touch it.</summary>
    private sealed class MergingServer(Store server) : ISyncProviderBase
    {
        public string[]? SyncTableNames => server.Provider.SyncTableNames;
        public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
            server.Provider.GetStoreIdAsync(ct);
        public Task SaveVersionForStoreAsync(Guid storeId, long version, CancellationToken ct = default) =>
            server.Provider.SaveVersionForStoreAsync(storeId, version, ct);
        public Task<SyncChangeSet> GetChangesAsync(
            Guid storeId, SyncFilterParameter[]? filters = null,
            SyncDirection direction = SyncDirection.UploadAndDownload,
            string[]? tables = null, CancellationToken ct = default) =>
            server.Provider.GetChangesAsync(storeId, filters, direction, tables, ct);

        public async Task<SyncAnchor> ApplyChangesAsync(
            SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
            CancellationToken ct = default)
        {
            IReadOnlySet<string> merged;
            await using (var db = server.OpenContext())
                merged = await VocabularyProgressConcurrentMerge.PrepareUploadAsync(
                    db, changes.Items, Owner, NullLogger.Instance, ct);
            var anchor = await server.Provider.ApplyChangesAsync(changes, item =>
                item.TableName == "VocabularyProgress" && item.ChangeType != ChangeType.Delete
                    && merged.Contains(item.Values["Id"].Value?.ToString() ?? string.Empty)
                    ? ConflictResolution.ForceWrite
                    : onConflict?.Invoke(item) ?? ConflictResolution.Skip, ct);
            await using (var db = server.OpenContext())
                await VocabularyProgressConcurrentMerge.TouchMergedAsync(
                    db, changes.Items, merged, Owner, ct);
            return anchor;
        }
    }

    private sealed class Store(
        SqliteConnection keepAlive, DbContextOptions<ApplicationDbContext> options,
        SqliteSyncProvider provider) : IAsyncDisposable
    {
        public SqliteSyncProvider Provider { get; } = provider;
        public ApplicationDbContext OpenContext() => new(options);

        public static async Task<Store> CreateAsync(
            ProviderMode mode, bool downloadOnlyProfile = false)
        {
            var connectionString =
                $"Data Source=native-merge-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            var options = DeviceInterpreterQueryGuard.Guard(new DbContextOptionsBuilder<ApplicationDbContext>())
                .UseSqlite(connectionString).Options;
            await using (var db = new ApplicationDbContext(options))
                await db.Database.EnsureCreatedAsync();
            var provider = new SqliteSyncProvider(
                new SqliteSyncConfigurationBuilder(connectionString)
                    .ConfigureSyncTables(downloadOnlyProfile).Build(), mode);
            await provider.ApplyProvisionAsync();
            return new Store(keepAlive, options, provider);
        }

        public ValueTask DisposeAsync() => keepAlive.DisposeAsync();
    }
}
