using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeLocalOnlyMappingExportProviderTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OtherOwner = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Resource = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Word = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const string ServerMapping = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";

    [Fact]
    public async Task LocalImportUndo_OriginalDeleteIsRejected_OnlyGhostDeletesAreCoalesced()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ghost1 = Guid.NewGuid().ToString();
        var ghost2 = Guid.NewGuid().ToString();
        var newResource = Guid.NewGuid().ToString();
        var liveMapping = Guid.NewGuid().ToString();
        var baselineAnchor = await fixture.LocalAnchorAsync();

        await fixture.InsertMappingAsync(ghost1);
        await fixture.InsertMappingAsync(ghost2);
        await fixture.DeleteMappingAsync(ghost1);
        await fixture.DeleteMappingAsync(ghost2);
        await using (var db = fixture.Local.OpenContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = newResource, UserProfileId = Owner, Title = "New resource"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = liveMapping, ResourceId = newResource, VocabularyWordId = Word
            });
            await db.SaveChangesAsync();
        }

        var original = await fixture.Local.Provider.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Equal([ghost1, ghost2],
            original.Items.Where(IsMappingDelete)
                .Select(item => item.Values["Id"].Value?.ToString()));

        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Local.Provider, fixture.Server).SynchronizeAsync());
        Assert.Equal(baselineAnchor, await fixture.LocalAnchorAsync());
        await using (var db = fixture.Remote.OpenContext())
            Assert.False(await db.LearningResources.AnyAsync(row => row.Id == newResource));

        var exported = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.DoesNotContain(exported.Items, IsMappingDelete);
        Assert.Contains(exported.Items, item => item.TableName == "LearningResource"
            && item.Values["Id"].Value?.ToString() == newResource);
        Assert.Contains(exported.Items, item => item.TableName == "ResourceVocabularyMapping"
            && item.ChangeType == ChangeType.Insert
            && item.Values["Id"].Value?.ToString() == liveMapping);
        Assert.Equal(original.SourceAnchor.Version, exported.SourceAnchor.Version);

        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        Assert.Equal(exported.SourceAnchor.Version, await fixture.LocalAnchorAsync());
        await using var remote = fixture.Remote.OpenContext();
        Assert.True(await remote.LearningResources.AnyAsync(row =>
            row.Id == newResource && row.UserProfileId == Owner));
        Assert.True(await remote.ResourceVocabularyMappings.AnyAsync(row =>
            row.Id == liveMapping && row.ResourceId == newResource));
        Assert.False(await remote.ResourceVocabularyMappings.AnyAsync(row =>
            row.Id == ghost1 || row.Id == ghost2));
    }

    [Fact]
    public async Task LaterCycles_AdvanceOnlyOnSuccessfulUpload_AndPreserveExistingDeletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(first);
        await fixture.DeleteMappingAsync(first);
        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        var firstAnchor = await fixture.LocalAnchorAsync();

        var second = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(second);
        await fixture.DeleteMappingAsync(second);
        fixture.Server.FailNextUpload = true;
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync());
        Assert.Equal(firstAnchor, await fixture.LocalAnchorAsync());

        var retry = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.DoesNotContain(retry.Items, IsMappingDelete);
        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        Assert.Equal(retry.SourceAnchor.Version, await fixture.LocalAnchorAsync());
        Assert.True(await fixture.LocalAnchorAsync() > firstAnchor);

        var alreadyUploaded = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(alreadyUploaded);
        await new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync();
        await fixture.DeleteMappingAsync(alreadyUploaded);
        var later = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(later.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == alreadyUploaded);
    }

    [Fact]
    public async Task CommittedRemoteInsertWithoutLocalAnchorSave_CannotBecomeLocalOnlyUndo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(id);
        var before = await fixture.LocalAnchorAsync();
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(new FailLocalSave(fixture.Native), fixture.Server)
                .SynchronizeAsync());
        Assert.Equal(before, await fixture.LocalAnchorAsync());
        await using (var remote = fixture.Remote.OpenContext())
            Assert.True(await remote.ResourceVocabularyMappings.AnyAsync(row => row.Id == id));

        await fixture.DeleteMappingAsync(id);
        var exported = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(exported.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == id);
    }

    [Fact]
    public async Task DownloadedServerMappingDeletion_RemainsQueuedAndServerRowIntact()
    {
        await using var fixture = await Fixture.CreateAsync(seedRemoteMapping: true);
        var anchor = await fixture.LocalAnchorAsync();
        await fixture.DeleteMappingAsync(ServerMapping);

        var exported = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(exported.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == ServerMapping);
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync());
        Assert.Equal(anchor, await fixture.LocalAnchorAsync());
        await using var remote = fixture.Remote.OpenContext();
        Assert.True(await remote.ResourceVocabularyMappings.AnyAsync(row =>
            row.Id == ServerMapping));
    }

    [Fact]
    public async Task ForeignResourcesOrAmbiguousMappingHistory_DoNotSilentlyDiscardDeletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reused = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(reused);
        await fixture.DeleteMappingAsync(reused);
        await fixture.InsertMappingAsync(reused);
        await fixture.DeleteMappingAsync(reused);
        var ambiguous = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(ambiguous.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == reused);

        var foreignResourceId = Guid.NewGuid().ToString();
        await using (var db = fixture.Local.OpenContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = foreignResourceId, UserProfileId = OtherOwner
            });
            await db.SaveChangesAsync();
        }
        var wrongOwner = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(wrongOwner, foreignResourceId);
        await fixture.DeleteMappingAsync(wrongOwner);
        var foreignParentPossible = await fixture.Native.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(foreignParentPossible.Items, item => IsMappingDelete(item)
            && item.Values["Id"].Value?.ToString() == wrongOwner);
    }

    [Fact]
    public async Task MissingOwnerOrRemoteReceipt_NeverAcknowledgesUnrelatedPendingResource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid().ToString();
        var newResource = Guid.NewGuid().ToString();
        await fixture.InsertMappingAsync(id);
        await fixture.DeleteMappingAsync(id);
        await using (var db = fixture.Local.OpenContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = newResource, UserProfileId = Owner
            });
            await db.SaveChangesAsync();
        }

        var noOwner = new NativeLocalOnlyMappingExportProvider(
            fixture.Local.Provider, fixture.Server, () => null,
            NullLogger<NativeLocalOnlyMappingExportProvider>.Instance);
        var unchanged = await noOwner.GetChangesAsync(
            fixture.RemoteId, syncDirection: SyncDirection.UploadOnly);
        Assert.Contains(unchanged.Items, IsMappingDelete);

        var anchor = await fixture.LocalAnchorAsync();
        fixture.Server.FailNextPreflight = true;
        await Assert.ThrowsAsync<SynchronizationException>(() =>
            new SyncAgent(fixture.Native, fixture.Server).SynchronizeAsync());
        Assert.Equal(anchor, await fixture.LocalAnchorAsync());
        await using (var db = fixture.Remote.OpenContext())
            Assert.False(await db.LearningResources.AnyAsync(row => row.Id == newResource));
    }

    private static bool IsMappingDelete(SyncItem item) =>
        item.TableName == "ResourceVocabularyMapping"
        && item.ChangeType == ChangeType.Delete;

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(Store local, Store remote)
        {
            Local = local;
            Remote = remote;
            Server = new RejectingRemote(remote.Provider);
            Native = new NativeLocalOnlyMappingExportProvider(
                local.Provider, Server, () => Owner,
                NullLogger<NativeLocalOnlyMappingExportProvider>.Instance);
        }

        public Store Local { get; }
        public Store Remote { get; }
        public RejectingRemote Server { get; }
        public ISyncProvider Native { get; }
        public Guid RemoteId => Remote.Provider.GetStoreIdAsync().GetAwaiter().GetResult();

        public static async Task<Fixture> CreateAsync(bool seedRemoteMapping = false)
        {
            var local = await Store.CreateAsync(ProviderMode.Local, downloadOnlyProfile: true);
            var remote = await Store.CreateAsync(ProviderMode.Remote);
            var fixture = new Fixture(local, remote);
            await using (var db = remote.OpenContext())
            {
                db.UserProfiles.Add(new UserProfile { Id = Owner, Email = "owner@example.test" });
                db.LearningResources.Add(new LearningResource
                {
                    Id = Resource, UserProfileId = Owner, Title = "Owned resource"
                });
                db.VocabularyWords.Add(new VocabularyWord
                {
                    Id = Word, TargetLanguageTerm = "practice", NativeLanguageTerm = "test"
                });
                if (seedRemoteMapping)
                    db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                    {
                        Id = ServerMapping, ResourceId = Resource, VocabularyWordId = Word
                    });
                await db.SaveChangesAsync();
            }
            await new SyncAgent(local.Provider, fixture.Server).SynchronizeAsync();
            return fixture;
        }

        public async Task<long> LocalAnchorAsync()
        {
            await using var connection = new SqliteConnection(Local.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT LOCAL_VERSION FROM __CORE_SYNC_REMOTE_ANCHOR WHERE ID = $id
                """;
            command.Parameters.AddWithValue("$id", RemoteId.ToString());
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task InsertMappingAsync(string id, string resourceId = Resource)
        {
            await using var db = Local.OpenContext();
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = id, ResourceId = resourceId, VocabularyWordId = Word
            });
            await db.SaveChangesAsync();
        }

        public async Task DeleteMappingAsync(string id)
        {
            await using var db = Local.OpenContext();
            var row = await db.ResourceVocabularyMappings.SingleAsync(mapping => mapping.Id == id);
            db.ResourceVocabularyMappings.Remove(row);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Local.DisposeAsync();
            await Remote.DisposeAsync();
        }
    }

    private sealed class Store(
        SqliteConnection keepAlive, string connectionString,
        DbContextOptions<ApplicationDbContext> options, SqliteSyncProvider provider)
        : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public SqliteSyncProvider Provider { get; } = provider;
        public ApplicationDbContext OpenContext() => new(options);

        public static async Task<Store> CreateAsync(
            ProviderMode mode, bool downloadOnlyProfile = false)
        {
            var connectionString =
                $"Data Source=native-undo-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString).Options;
            await using (var db = new ApplicationDbContext(options))
                await db.Database.EnsureCreatedAsync();
            var provider = new SqliteSyncProvider(
                new SqliteSyncConfigurationBuilder(connectionString)
                    .ConfigureSyncTables(downloadOnlyProfile).Build(), mode);
            await provider.ApplyProvisionAsync();
            return new Store(keepAlive, connectionString, options, provider);
        }

        public ValueTask DisposeAsync() => keepAlive.DisposeAsync();
    }

    private sealed class RejectingRemote(ISyncProvider inner) : ISyncProviderBase
    {
        public bool FailNextUpload { get; set; }
        public bool FailNextPreflight { get; set; }
        public string[]? SyncTableNames => inner.SyncTableNames;
        public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
            inner.GetStoreIdAsync(ct);
        public Task SaveVersionForStoreAsync(
            Guid storeId, long version, CancellationToken ct = default) =>
            inner.SaveVersionForStoreAsync(storeId, version, ct);
        public Task<SyncChangeSet> GetChangesAsync(
            Guid storeId, SyncFilterParameter[]? filters = null,
            SyncDirection direction = SyncDirection.UploadAndDownload,
            string[]? tables = null, CancellationToken ct = default)
        {
            if (FailNextPreflight && tables is ["UserProfile"])
            {
                FailNextPreflight = false;
                throw new InvalidOperationException("Remote receipt unavailable.");
            }
            return inner.GetChangesAsync(storeId, filters, direction, tables, ct);
        }
        public Task<SyncAnchor> ApplyChangesAsync(
            SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
            CancellationToken ct = default)
        {
            if (changes.Items.Any(IsMappingDelete))
                throw new UnauthorizedAccessException("Native server refuses Delete (403).");
            if (FailNextUpload)
            {
                FailNextUpload = false;
                throw new InvalidOperationException("Upload interrupted before server commit.");
            }
            return inner.ApplyChangesAsync(changes, onConflict, ct);
        }
    }

    private sealed class FailLocalSave(ISyncProviderBase inner) : ISyncProviderBase
    {
        public string[]? SyncTableNames => inner.SyncTableNames;
        public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
            inner.GetStoreIdAsync(ct);
        public Task SaveVersionForStoreAsync(
            Guid storeId, long version, CancellationToken ct = default) =>
            throw new InvalidOperationException("Interrupted after server commit.");
        public Task<SyncChangeSet> GetChangesAsync(
            Guid storeId, SyncFilterParameter[]? filters = null,
            SyncDirection direction = SyncDirection.UploadAndDownload,
            string[]? tables = null, CancellationToken ct = default) =>
            inner.GetChangesAsync(storeId, filters, direction, tables, ct);
        public Task<SyncAnchor> ApplyChangesAsync(
            SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
            CancellationToken ct = default) =>
            inner.ApplyChangesAsync(changes, onConflict, ct);
    }
}
