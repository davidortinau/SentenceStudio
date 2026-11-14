using CoreSync;
using CoreSync.Http.Client;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// The MAUI heads' sync path end to end (Captain 2026-11-04, decision 1): the shared
/// <see cref="SyncService"/> over a single-database SQLite store holding what an installed MAUI
/// app holds (the learner's rows, another learner's rows downloaded before owner checks, rows
/// with no owner, a legacy table), with <see cref="OwnerCheckedSyncProvider"/> and
/// <see cref="SyncOwnerHeaderHandler"/> as AppLib's AddOwnerCheckedSync wires them, against the
/// real API with headerless sync refused, on throwaway PostgreSQL.
/// </summary>
public sealed class MauiOwnerCheckedSyncPostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public MauiOwnerCheckedSyncPostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task MauiDatabase_SyncsTheLearnersRows_KeepsTheRestLocal_AndRefusesASecondAccount()
    {
        var (owner, other) = await _fixture.SeedOwnersAsync();
        var webResource = Guid.NewGuid().ToString();
        var otherServerResource = Guid.NewGuid().ToString();
        await using (var server = _fixture.NewContext())
        {
            // Made on the web after the device last synced.
            server.LearningResources.AddRange(
                new LearningResource { Id = webResource, UserProfileId = owner, Title = "From the web" },
                new LearningResource { Id = otherServerResource, UserProfileId = other, Title = "Theirs" });
            await server.SaveChangesAsync();
        }

        await using var device = await MauiDevice.CreateAsync(_fixture);
        var rows = await device.SeedMauiShapedRowsAsync(owner, other);

        await device.SyncAsync(owner);

        await using (var server = _fixture.NewContext())
        {
            // The learner's rows reached the server.
            Assert.True(await server.LearningResources.AnyAsync(row =>
                row.Id == rows.Resource && row.UserProfileId == owner));
            Assert.True(await server.VocabularyWords.AnyAsync(row => row.Id == rows.Word));
            Assert.True(await server.ResourceVocabularyMappings.AnyAsync(row => row.Id == rows.Mapping));
            Assert.True(await server.VocabularyProgresses.AnyAsync(row =>
                row.Id == rows.Progress && row.UserId == owner));
            Assert.True(await server.MinimalPairs.AnyAsync(row =>
                row.Id == rows.Pair && row.UserId == owner));
            Assert.Equal(SyncIds.BuiltInScenario("First Meeting"), (await server.Conversations
                .AsNoTracking().SingleAsync(row => row.Id == rows.Conversation)).ScenarioId);
            // Everything else stayed on the device.
            Assert.False(await server.LearningResources.AnyAsync(row => row.Id == rows.OtherResource));
            Assert.False(await server.VocabularyProgresses.AnyAsync(row => row.Id == rows.OtherProgress));
            Assert.False(await server.SceneImages.AnyAsync(row => row.Id == rows.UnownedImage));
            Assert.False(await server.VocabularyLists.AnyAsync(row => row.Id == rows.LegacyList));
            // Words go by their recorded owner (Captain 2026-11-06): the learner's word in no
            // resource syncs too; another learner's word on this install does not.
            Assert.True(await server.VocabularyWords.AnyAsync(row =>
                row.Id == rows.OrphanWord && row.UserProfileId == owner));
            Assert.False(await server.VocabularyWords.AnyAsync(row => row.Id == rows.OtherWord));
            Assert.False(await server.ConversationScenarios.AnyAsync(row => row.Id == rows.BuiltInScenario));
            Assert.Equal("Theirs", (await server.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == otherServerResource)).Title);
        }

        // Nothing on the device was deleted or re-owned; the web's row arrived, nobody else's did.
        await using (var local = device.OpenContext())
        {
            Assert.Equal(rows.LocalRowCount, await rows.CountLocalAsync(local));
            Assert.Equal(other, (await local.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == rows.OtherResource)).UserProfileId);
            Assert.Null((await local.SceneImages.AsNoTracking()
                .SingleAsync(row => row.Id == rows.UnownedImage)).UserProfileId);
            Assert.True(await local.LearningResources.AnyAsync(row => row.Id == webResource));
            Assert.False(await local.LearningResources.AnyAsync(row => row.Id == otherServerResource));
        }

        // Sync is not stuck: a later change syncs too.
        await using (var local = device.OpenContext())
        {
            var resource = await local.LearningResources.SingleAsync(row => row.Id == rows.Resource);
            resource.Title = "Renamed on MAUI";
            await local.SaveChangesAsync();
        }
        await device.SyncAsync(owner);
        await using (var server = _fixture.NewContext())
            Assert.Equal("Renamed on MAUI", (await server.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == rows.Resource)).Title);

        // A second account on the same install is refused and writes nothing.
        await using (var local = device.OpenContext())
        {
            local.LearningResources.Add(new LearningResource
            {
                Id = Guid.NewGuid().ToString(), UserProfileId = other, Title = "Second account",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await local.SaveChangesAsync();
        }
        var refused = await Assert.ThrowsAsync<SynchronizationException>(() => device.SyncAsync(other));
        var http = Assert.IsType<HttpRequestException>(refused.InnerException);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, http.StatusCode);
        await using (var server = _fixture.NewContext())
            Assert.False(await server.LearningResources.AnyAsync(row => row.Title == "Second account"));

        // The first account keeps syncing; the second account's row stays on the device.
        await device.SyncAsync(owner);
        await using (var server = _fixture.NewContext())
            Assert.False(await server.LearningResources.AnyAsync(row => row.Title == "Second account"));
        await using (var local = device.OpenContext())
            Assert.True(await local.LearningResources.AnyAsync(row => row.Title == "Second account"));
    }

    /// <summary>
    /// Duotopia 2026-11-13: a duplicate merge on the web moved a word's answers to the kept word;
    /// an install that applied the downloaded delete logged the answers' cascade as its own and
    /// uploaded it, deleting them on the server (docs/progress-count-repair-2026-11-13.md).
    /// </summary>
    [ApplicationOperationPostgresFact]
    public async Task MergeOnTheWeb_DeletesNoMovedAnswer_WhenAnInstallDownloadsIt()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var resource = Guid.NewGuid().ToString();
        var (kept, duplicate) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var (keptProgress, duplicateProgress) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var moved = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var now = DateTime.UtcNow;
        await using (var server = _fixture.NewContext())
        {
            server.LearningResources.Add(new LearningResource { Id = resource, UserProfileId = owner, Title = "Mine" });
            foreach (var (word, progress) in new[] { (kept, keptProgress), (duplicate, duplicateProgress) })
            {
                server.VocabularyWords.Add(new VocabularyWord { Id = word, UserProfileId = owner, TargetLanguageTerm = "먹다" });
                server.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                    { Id = Guid.NewGuid().ToString(), ResourceId = resource, VocabularyWordId = word });
                server.VocabularyProgresses.Add(new VocabularyProgress
                {
                    Id = progress, UserId = owner, VocabularyWordId = word, TotalAttempts = 1,
                    FirstSeenAt = now, LastPracticedAt = now, CreatedAt = now, UpdatedAt = now
                });
            }
            server.VocabularyLearningContexts.AddRange(moved.Select(id => new VocabularyLearningContext
            {
                Id = id, VocabularyProgressId = duplicateProgress, Activity = "VocabularyQuiz",
                InputMode = "MultipleChoice", WasCorrect = true, LearnedAt = now, CreatedAt = now, UpdatedAt = now
            }));
            await server.SaveChangesAsync();
        }
        await using var mac = await MauiDevice.CreateAsync(_fixture);
        await mac.SyncAsync(owner);

        // The web merge's history move (LearningResourceRepository): answers to the kept word's
        // progress, then the duplicate's progress and word removed.
        await using (var server = _fixture.NewContext())
        {
            foreach (var context in await server.VocabularyLearningContexts
                         .Where(context => moved.Contains(context.Id)).ToListAsync())
                context.VocabularyProgressId = keptProgress;
            await server.SaveChangesAsync();
            server.VocabularyProgresses.Remove(await server.VocabularyProgresses.SingleAsync(row => row.Id == duplicateProgress));
            server.VocabularyWords.Remove(await server.VocabularyWords.SingleAsync(row => row.Id == duplicate));
            await server.SaveChangesAsync();
        }
        await mac.SyncAsync(owner);
        await mac.SyncAsync(owner);

        await using (var server = _fixture.NewContext())
            Assert.Equal(2, await server.VocabularyLearningContexts
                .CountAsync(context => moved.Contains(context.Id) && context.VocabularyProgressId == keptProgress));
        // The install logged the download's cascade as the server's: nothing of it waits to upload.
        await using (var local = mac.OpenContext())
            Assert.Equal(0, await local.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS \"Value\" FROM __CORE_SYNC_CT WHERE OP = 'D' AND SRC IS NULL").SingleAsync());
    }

    [ApplicationOperationPostgresFact]
    public async Task DeleteOnOneInstall_ReachesTheLearnersOtherInstall()
    {
        var (owner, other) = await _fixture.SeedOwnersAsync();
        await using var phone = await MauiDevice.CreateAsync(_fixture);
        await using var mac = await MauiDevice.CreateAsync(_fixture);
        var rows = await phone.SeedMauiShapedRowsAsync(owner, other);
        await phone.SyncAsync(owner);
        await mac.SyncAsync(owner);
        await using (var local = mac.OpenContext())
        {
            Assert.True(await local.ResourceVocabularyMappings.AnyAsync(row => row.Id == rows.Mapping));
            Assert.True(await local.MinimalPairs.AnyAsync(row => row.Id == rows.Pair));
        }

        // On the phone: remove a word from the resource and delete the minimal pair.
        await using (var local = phone.OpenContext())
        {
            local.ResourceVocabularyMappings.Remove(
                await local.ResourceVocabularyMappings.SingleAsync(row => row.Id == rows.Mapping));
            local.MinimalPairs.Remove(await local.MinimalPairs.SingleAsync(row => row.Id == rows.Pair));
            await local.SaveChangesAsync();
        }
        await phone.SyncAsync(owner);

        await using (var server = _fixture.NewContext())
        {
            Assert.False(await server.ResourceVocabularyMappings.AnyAsync(row => row.Id == rows.Mapping));
            Assert.False(await server.MinimalPairs.AnyAsync(row => row.Id == rows.Pair));
        }
        await mac.SyncAsync(owner);
        await using (var local = mac.OpenContext())
        {
            Assert.False(await local.ResourceVocabularyMappings.AnyAsync(row => row.Id == rows.Mapping));
            Assert.False(await local.MinimalPairs.AnyAsync(row => row.Id == rows.Pair));
            Assert.True(await local.LearningResources.AnyAsync(row => row.Id == rows.Resource));
        }
    }

    [ApplicationOperationPostgresFact]
    public async Task SharedOldWord_RecordedToNoOne_IsLeftOut_AndSyncIsNotStuck()
    {
        // An old word with no recorded owner, linked from another learner's resource. On the
        // MAUI install the learner also practised and edited it. It is no one's (the one
        // ownership rule): the device holds it back, the server leaves the learner's progress on
        // it out, and the row must not stop this install's sync.
        var (owner, other) = await _fixture.SeedOwnersAsync();
        var word = Guid.NewGuid().ToString();
        var otherResource = Guid.NewGuid().ToString();
        var link = Guid.NewGuid().ToString();
        await using (var server = _fixture.NewContext())
        {
            server.LearningResources.Add(new LearningResource { Id = otherResource, UserProfileId = other, Title = "Theirs" });
            await server.SaveChangesAsync();
            // SQL, so the server records no owner for the old word.
            await server.Database.ExecuteSqlRawAsync("""
                INSERT INTO "VocabularyWord" ("Id", "TargetLanguageTerm", "CreatedAt", "UpdatedAt", "LexicalUnitType")
                    VALUES ({0}, 'shared', now(), now(), 0);
                INSERT INTO "ResourceVocabularyMapping" ("Id", "ResourceId", "VocabularyWordId")
                    VALUES ({1}, {2}, {0});
                """, word, link, otherResource);
        }

        await using var device = await MauiDevice.CreateAsync(_fixture);
        var progress = Guid.NewGuid().ToString();
        var ownResource = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        // The old word as the install holds it: saved before words had owners (a save can no
        // longer create one, so SQL clears the owner the way the old rows were left).
        await using (var local = device.OpenContext())
        {
            local.LearningResources.Add(new LearningResource { Id = otherResource, UserProfileId = other, Title = "Theirs", CreatedAt = now, UpdatedAt = now });
            local.VocabularyWords.Add(new VocabularyWord { Id = word, TargetLanguageTerm = "shared", UserProfileId = owner, CreatedAt = now, UpdatedAt = now });
            await local.SaveChangesAsync();
            await local.VocabularyWords.Where(row => row.Id == word)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.UserProfileId, (string?)null));
        }
        await using (var local = device.OpenContext())
        {
            local.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping { Id = link, ResourceId = otherResource, VocabularyWordId = word });
            await local.SaveChangesAsync();
        }
        await using (var local = device.OpenContext())
        {
            local.VocabularyProgresses.Add(new VocabularyProgress { Id = progress, UserId = owner, VocabularyWordId = word, CreatedAt = now, UpdatedAt = now });
            local.LearningResources.Add(new LearningResource { Id = ownResource, UserProfileId = owner, Title = "Mine", CreatedAt = now, UpdatedAt = now });
            await local.SaveChangesAsync();
        }
        await using (var local = device.OpenContext())
        {
            var stored = await local.VocabularyWords.SingleAsync(row => row.Id == word);
            Assert.Null(stored.UserProfileId);
            stored.TargetLanguageTerm = "edited on MAUI";
            await local.SaveChangesAsync();
        }
        await using (var local = device.OpenContext())
            Assert.Null((await SyncRowOwners.GetStoredWordOwnersAsync(local, [word], default))[word]);

        await device.SyncAsync(owner);

        await using (var server = _fixture.NewContext())
        {
            Assert.Equal("shared", (await server.VocabularyWords.AsNoTracking()
                .SingleAsync(row => row.Id == word)).TargetLanguageTerm);
            Assert.False(await server.VocabularyProgresses.AnyAsync(row => row.Id == progress));
            Assert.True(await server.LearningResources.AnyAsync(row => row.Id == ownResource));
        }
        // Not stuck: the next change syncs too, and the device kept its own copies.
        await using (var local = device.OpenContext())
        {
            (await local.LearningResources.SingleAsync(row => row.Id == ownResource)).Title = "Renamed";
            await local.SaveChangesAsync();
        }
        await device.SyncAsync(owner);
        await using (var server = _fixture.NewContext())
            Assert.Equal("Renamed", (await server.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == ownResource)).Title);
        await using (var local = device.OpenContext())
        {
            Assert.Equal("edited on MAUI", (await local.VocabularyWords.AsNoTracking()
                .SingleAsync(row => row.Id == word)).TargetLanguageTerm);
            Assert.True(await local.VocabularyProgresses.AnyAsync(row => row.Id == progress));
        }
    }

    [ApplicationOperationPostgresFact]
    public async Task NoSignedInLearner_SendsNothing()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        await using var device = await MauiDevice.CreateAsync(_fixture);
        var rows = await device.SeedMauiShapedRowsAsync(owner, Guid.NewGuid().ToString());

        var refused = await Assert.ThrowsAsync<SynchronizationException>(() => device.SyncAsync(owner: null));
        Assert.IsType<UnauthorizedAccessException>(refused.InnerException);
        Assert.Equal(0, device.RequestsSent);
        await using var server = _fixture.NewContext();
        Assert.False(await server.LearningResources.AnyAsync(row => row.Id == rows.Resource));
    }

    /// <summary>A MAUI-shaped device: one SQLite database and store for every account.</summary>
    private sealed class MauiDevice : IAsyncDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly ServiceProvider _services;
        private readonly SwitchableOwner _owner;
        private readonly RequestCounter _counter;

        private MauiDevice(SqliteConnection keepAlive, DbContextOptions<ApplicationDbContext> options,
            ServiceProvider services, SwitchableOwner owner, RequestCounter counter)
        {
            _keepAlive = keepAlive;
            Options = options;
            _services = services;
            _owner = owner;
            _counter = counter;
        }

        private DbContextOptions<ApplicationDbContext> Options { get; }
        public int RequestsSent => _counter.Count;
        public ApplicationDbContext OpenContext() => new(Options);

        public static async Task<MauiDevice> CreateAsync(NativeOwnerSyncPostgresFixture fixture)
        {
            var connectionString =
                $"Data Source=maui-sync-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString).Options;
            await using (var db = new ApplicationDbContext(options))
                await db.Database.EnsureCreatedAsync();

            var owner = new SwitchableOwner();
            var counter = new RequestCounter();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(builder => builder.UseSqlite(connectionString));
            services.AddSingleton<ISyncOwnerSource>(owner);
            // As AppLib's AddSyncServices + AddOwnerCheckedSync register them for MAUI.
            services.AddSingleton<ISyncProvider>(provider => new OwnerCheckedSyncProvider(
                new SqliteSyncProvider(new SqliteSyncConfigurationBuilder(connectionString)
                    .ConfigureSyncTables().Build(), ProviderMode.Local),
                () => new ApplicationDbContext(options), owner,
                provider.GetRequiredService<ILogger<OwnerCheckedSyncProvider>>()));
            services.AddTransient(_ => new SyncOwnerHeaderHandler(owner));
            services.AddTransient(_ => new CountingHandler(counter));
            services.AddHttpClient("HttpClientToServer", client => client.BaseAddress = fixture.ServerAddress)
                .ConfigurePrimaryHttpMessageHandler(fixture.CreateServerHandler)
                .AddHttpMessageHandler<SyncOwnerHeaderHandler>()
                .AddHttpMessageHandler<CountingHandler>();
            services.AddCoreSyncHttpClient(options => options.HttpClientName = "HttpClientToServer");
            services.AddSingleton<SyncService>();
            return new MauiDevice(keepAlive, options, services.BuildServiceProvider(), owner, counter);
        }

        public async Task SyncAsync(string? owner)
        {
            _owner.Current = owner is null ? null
                : new SyncOwnerCredentials(owner, TestJwtGenerator.GenerateToken(userProfileId: owner));
            await _services.GetRequiredService<SyncService>()
                .SynchronizeForReadinessAsync(CancellationToken.None);
        }

        public async Task<MauiRows> SeedMauiShapedRowsAsync(string owner, string other)
        {
            var now = DateTime.UtcNow;
            var rows = new MauiRows(owner, other);
            await using var db = OpenContext();
            db.UserProfiles.AddRange(
                new UserProfile { Id = owner, Email = $"{owner}@example.test" },
                new UserProfile { Id = other, Email = $"{other}@example.test" });
            db.LearningResources.AddRange(
                new LearningResource { Id = rows.Resource, UserProfileId = owner, Title = "Mine", CreatedAt = now, UpdatedAt = now },
                // Downloaded before owner checks: another learner's resource.
                new LearningResource { Id = rows.OtherResource, UserProfileId = other, Title = "Theirs", CreatedAt = now, UpdatedAt = now });
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = rows.Word, TargetLanguageTerm = "물", NativeLanguageTerm = "water", UserProfileId = owner, CreatedAt = now, UpdatedAt = now },
                new VocabularyWord { Id = rows.SecondWord, TargetLanguageTerm = "불", NativeLanguageTerm = "fire", UserProfileId = owner, CreatedAt = now, UpdatedAt = now },
                // A lookup saved as a word before B7: owned but in no resource.
                new VocabularyWord { Id = rows.OrphanWord, TargetLanguageTerm = "꽃", NativeLanguageTerm = "flower", UserProfileId = owner, CreatedAt = now, UpdatedAt = now },
                // Downloaded before owner checks: another learner's word.
                new VocabularyWord { Id = rows.OtherWord, TargetLanguageTerm = "산", NativeLanguageTerm = "mountain", UserProfileId = other, CreatedAt = now, UpdatedAt = now });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping { Id = rows.Mapping, ResourceId = rows.Resource, VocabularyWordId = rows.Word },
                new ResourceVocabularyMapping { Id = Guid.NewGuid().ToString(), ResourceId = rows.Resource, VocabularyWordId = rows.SecondWord },
                new ResourceVocabularyMapping { Id = Guid.NewGuid().ToString(), ResourceId = rows.OtherResource, VocabularyWordId = rows.OtherWord });
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress { Id = rows.Progress, UserId = owner, VocabularyWordId = rows.Word, CreatedAt = now, UpdatedAt = now },
                new VocabularyProgress { Id = rows.OtherProgress, UserId = other, VocabularyWordId = rows.OtherWord, CreatedAt = now, UpdatedAt = now });
            db.ConversationScenarios.Add(new ConversationScenario
            {
                Id = rows.BuiltInScenario, Name = "First Meeting", PersonaName = "P",
                PersonaDescription = "D", SituationDescription = "S", IsPredefined = true,
                CreatedAt = now, UpdatedAt = now
            });
            db.Conversations.Add(new Conversation
            {
                Id = rows.Conversation, UserProfileId = owner, ScenarioId = rows.BuiltInScenario,
                CreatedAt = now
            });
            db.MinimalPairs.Add(new MinimalPair
            {
                Id = rows.Pair, UserId = owner, VocabularyWordAId = rows.Word,
                VocabularyWordBId = rows.SecondWord, CreatedAt = now, UpdatedAt = now
            });
            // Saved before scene images had an owner (D3).
            db.SceneImages.Add(new SceneImage { Id = rows.UnownedImage, Url = "https://example.test/old.jpg" });
            db.VocabularyLists.Add(new VocabularyList { Id = rows.LegacyList, Name = "Legacy list" });
            await db.SaveChangesAsync();
            rows.LocalRowCount = await rows.CountLocalAsync(db);
            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _keepAlive.DisposeAsync();
        }
    }

    private sealed class MauiRows(string owner, string other)
    {
        public string Owner { get; } = owner;
        public string Other { get; } = other;
        public string Resource { get; } = Guid.NewGuid().ToString();
        public string OtherResource { get; } = Guid.NewGuid().ToString();
        public string Word { get; } = Guid.NewGuid().ToString();
        public string SecondWord { get; } = Guid.NewGuid().ToString();
        public string OrphanWord { get; } = Guid.NewGuid().ToString();
        public string OtherWord { get; } = Guid.NewGuid().ToString();
        public string Mapping { get; } = Guid.NewGuid().ToString();
        public string Progress { get; } = Guid.NewGuid().ToString();
        public string OtherProgress { get; } = Guid.NewGuid().ToString();
        public string Conversation { get; } = Guid.NewGuid().ToString();
        public string BuiltInScenario { get; } = SyncIds.BuiltInScenario("First Meeting");
        public string Pair => SyncIds.MinimalPair(Owner, Word, SecondWord);
        public string UnownedImage { get; } = Guid.NewGuid().ToString();
        public string LegacyList { get; } = Guid.NewGuid().ToString();
        public int LocalRowCount { get; set; }

        public async Task<int> CountLocalAsync(ApplicationDbContext db) =>
            await db.UserProfiles.CountAsync(row => row.Id == Owner || row.Id == Other)
            + await db.LearningResources.CountAsync(row => row.Id == Resource || row.Id == OtherResource)
            + await db.VocabularyWords.CountAsync(row => row.Id == Word || row.Id == SecondWord
                || row.Id == OrphanWord || row.Id == OtherWord)
            + await db.VocabularyProgresses.CountAsync(row => row.Id == Progress || row.Id == OtherProgress)
            + await db.ConversationScenarios.CountAsync(row => row.Id == BuiltInScenario)
            + await db.Conversations.CountAsync(row => row.Id == Conversation)
            + await db.MinimalPairs.CountAsync(row => row.Id == Pair)
            + await db.SceneImages.CountAsync(row => row.Id == UnownedImage)
            + await db.VocabularyLists.CountAsync(row => row.Id == LegacyList);
    }

    private sealed class SwitchableOwner : ISyncOwnerSource
    {
        public SyncOwnerCredentials? Current { get; set; }

        public Task<SyncOwnerCredentials?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }

    private sealed class RequestCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Add() => Interlocked.Increment(ref _count);
    }

    /// <summary>Counts requests that actually leave the owner header handler.</summary>
    private sealed class CountingHandler(RequestCounter counter) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            counter.Add();
            return base.SendAsync(request, cancellationToken);
        }
    }
}
