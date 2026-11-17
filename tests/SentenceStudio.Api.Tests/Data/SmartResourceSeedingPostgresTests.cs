using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Data;

/// <summary>
/// The smart lists (Daily Review, New Words Practice, Struggling Words, Phrases, Sentences) are
/// made once per learner, however many seeds run at the same time.
/// </summary>
/// <remarks>
/// Seen on 2026-11-16 on a fresh database: a new learner had fourteen smart lists, the same five
/// two or three times over. A page load asks for the profile from several components at once and
/// every ask seeds; the seed checked which types exist and inserted the missing ones as two
/// separate steps. These run on PostgreSQL because SQLite's provider completes its async calls
/// synchronously, so the seeds never interleave there and the fault cannot show.
/// </remarks>
public sealed class SmartResourceSeedingPostgresTests
{
    private const string Learner = "seed-learner-a";
    private const string OtherLearner = "seed-learner-b";

    [ApplicationOperationPostgresFact]
    public async Task ManySeedsAtOnce_InOneProcess_MakeEachListOnce()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_seed_one");
        await using var app = App(database.ConnectionString);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => SeedAsync(app, Learner)));

        await AssertEachListOnceAsync(database, Learner);
    }

    /// <summary>
    /// Two instances of the app serving the same learner. Each has its own service, so only the
    /// database lock keeps them from both seeding.
    /// </summary>
    [ApplicationOperationPostgresFact]
    public async Task SeedsFromTwoInstancesAtOnce_MakeEachListOnce()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_seed_two");
        await using var first = App(database.ConnectionString);
        await using var second = App(database.ConnectionString);

        await Task.WhenAll(Enumerable.Range(0, 6)
            .SelectMany(_ => new[] { SeedAsync(first, Learner), SeedAsync(second, Learner) }));

        await AssertEachListOnceAsync(database, Learner);
    }

    [ApplicationOperationPostgresFact]
    public async Task TwoLearnersSeededAtOnce_EachGetsTheirOwnLists()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_seed_owner");
        await using var app = App(database.ConnectionString);

        await Task.WhenAll(Enumerable.Range(0, 4)
            .SelectMany(_ => new[] { SeedAsync(app, Learner), SeedAsync(app, OtherLearner) }));

        await AssertEachListOnceAsync(database, Learner);
        await AssertEachListOnceAsync(database, OtherLearner);
    }

    [ApplicationOperationPostgresFact]
    public async Task ASeedAfterTheListsExist_MakesNothing()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_seed_again");
        await using var app = App(database.ConnectionString);

        await SeedAsync(app, Learner);
        var ids = await ListIdsAsync(database, Learner);
        await SeedAsync(app, Learner);

        (await ListIdsAsync(database, Learner)).Should().BeEquivalentTo(ids);
    }

    [ApplicationOperationPostgresFact]
    public async Task NewLists_HaveTheLearnersFixedIds()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_seed_ids");
        await using var app = App(database.ConnectionString);

        await SeedAsync(app, Learner);

        (await ListIdsAsync(database, Learner)).Should().BeEquivalentTo(AllTypes.Select(type => SyncIds.SmartResource(Learner, type)),
            "a phone that seeds before its first sync makes the same rows");
    }

    /// <summary>
    /// Copies stored before the fix (Captain, 2026-11-16: one set per learner) are folded into one
    /// while several seeds run: the lowest ID is kept (not the oldest, whose stored time may be a
    /// local wall clock), what named a copy moves to it, it is refilled, and each delete is in the
    /// deletion log, which is how the learner's other devices drop the copy too.
    /// </summary>
    [ApplicationOperationPostgresFact]
    public async Task StoredCopies_ManySeedsAtOnce_FoldIntoOne_AndTheDeletesReachOtherDevices()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_seed_fold");
        await using (var db = database.NewContext())
        {
            db.LearningResources.AddRange(
                SmartList("review-1", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 30),
                SmartList("review-2", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 90),
                SmartList("review-3", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60),
                SmartList("phrases-1", Learner, SmartResourceService.SmartResourceType_Phrases, minutesAgo: 90),
                SmartList("phrases-2", Learner, SmartResourceService.SmartResourceType_Phrases, minutesAgo: 60),
                SmartList("other-review-1", OtherLearner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 90),
                SmartList("other-review-2", OtherLearner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60));
            // A word due today, in one of the learner's own lists and in a copy of Daily Review.
            var ownList = new LearningResource { Id = "own-list", UserProfileId = Learner, Title = "Own", Language = "Korean" };
            db.LearningResources.Add(ownList);
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "word-1", UserProfileId = Learner, TargetLanguageTerm = "사과", NativeLanguageTerm = "apple",
                Language = "Korean", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping { Id = "map-own", ResourceId = "own-list", VocabularyWordId = "word-1" },
                new ResourceVocabularyMapping { Id = "map-copy", ResourceId = "review-3", VocabularyWordId = "word-1" });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = "progress-1", VocabularyWordId = "word-1", UserId = Learner, MasteryScore = 0.3f,
                TotalAttempts = 5, CorrectAttempts = 2, NextReviewDate = DateTime.UtcNow.AddDays(-1),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            db.VocabularyLookups.Add(new VocabularyLookup
            {
                Id = "lookup-1", UserProfileId = Learner, Text = "apple", Translation = "사과", Language = "Korean",
                Activity = "Reading", ResourceId = "review-3", FirstLookedUpAt = DateTime.UtcNow, LastLookedUpAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await using var app = App(database.ConnectionString);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => SeedAsync(app, Learner)));

        await AssertEachListOnceAsync(database, Learner);
        var ids = await ListIdsAsync(database, Learner);
        ids.Should().Contain(["review-1", "phrases-1"], "the lowest ID of each type is kept");

        await using var check = database.NewContext();
        (await check.VocabularyLookups.AsNoTracking().SingleAsync(l => l.Id == "lookup-1")).ResourceId
            .Should().Be("review-1", "what named a deleted copy now names the kept list");
        (await check.ResourceVocabularyMappings.AsNoTracking().AnyAsync(m => m.ResourceId == "review-1" && m.VocabularyWordId == "word-1"))
            .Should().BeTrue("the kept list is refilled: the copy's links went with it");
        (await ListIdsAsync(database, OtherLearner)).Should().BeEquivalentTo(["other-review-1", "other-review-2"],
            "a seed for one learner touches only their lists");

        var logged = await check.Database.SqlQueryRaw<LoggedDeletion>(
                "SELECT \"RowId\", \"OwnerId\" FROM \"SyncDeletion\" WHERE \"TableName\" = 'LearningResource'")
            .ToListAsync();
        logged.Should().BeEquivalentTo(
            new[] { "review-2", "review-3", "phrases-2" }.Select(id => new LoggedDeletion { RowId = id, OwnerId = Learner }),
            "each deleted copy is logged once for the learner's other devices");
    }

    /// <summary>
    /// A learner's list made before the fixed IDs (a GUID, which every device already uses) is
    /// kept over a copy a new phone made with the fixed ID before its first sync: the phone's
    /// copy is the one folded, so the learner's other devices keep their list.
    /// </summary>
    [ApplicationOperationPostgresFact]
    public async Task StoredCopies_TheEstablishedListIsKept_OverANewPhonesCopy()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_seed_fixed");
        var established = "ffffffff-0000-4000-8000-000000000001";
        var phoneCopy = SyncIds.SmartResource(Learner, SmartResourceService.SmartResourceType_NewWords);
        await using (var db = database.NewContext())
        {
            db.LearningResources.AddRange(
                SmartList(established, Learner, SmartResourceService.SmartResourceType_NewWords, minutesAgo: 1),
                SmartList(phoneCopy, Learner, SmartResourceService.SmartResourceType_NewWords, minutesAgo: 600));
            await db.SaveChangesAsync();
        }
        await using var app = App(database.ConnectionString);

        await SeedAsync(app, Learner);

        await AssertEachListOnceAsync(database, Learner);
        (await ListIdsAsync(database, Learner)).Should().Contain(established).And.NotContain(phoneCopy);
    }

    /// <summary>
    /// The fold waits for a sync upload in progress (they share the upload lock), so an upload
    /// cannot write a row naming a copy between the fold's move and its delete.
    /// </summary>
    [ApplicationOperationPostgresFact]
    public async Task Folding_WaitsForAnUploadInProgress()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("smart_fold_lock");
        await using (var db = database.NewContext())
        {
            db.LearningResources.AddRange(
                SmartList("review-1", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60),
                SmartList("review-2", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 30));
            await db.SaveChangesAsync();
        }
        await using var app = App(database.ConnectionString);

        // An upload holds the lock as the sync provider does: a session lock on its own connection.
        await using var upload = new NpgsqlConnection(database.ConnectionString);
        await upload.OpenAsync();
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", upload))
        {
            take.Parameters.AddWithValue("key", SyncUploadLock.Key);
            await take.ExecuteNonQueryAsync();
        }

        var fold = app.GetRequiredService<LearningResourceRepository>()
            .MergeSmartResourceCopiesAsync("review-1", ["review-2"], Learner);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        fold.IsCompleted.Should().BeFalse("the fold waits while an upload holds the lock");
        (await ListIdsAsync(database, Learner)).Should().Contain("review-2");

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", upload))
        {
            release.Parameters.AddWithValue("key", SyncUploadLock.Key);
            await release.ExecuteNonQueryAsync();
        }
        (await fold.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(1);
        (await ListIdsAsync(database, Learner)).Should().Equal(["review-1"]);
    }

    private static readonly string[] AllTypes =
    [
        SmartResourceService.SmartResourceType_DailyReview,
        SmartResourceService.SmartResourceType_NewWords,
        SmartResourceService.SmartResourceType_Struggling,
        SmartResourceService.SmartResourceType_Phrases,
        SmartResourceService.SmartResourceType_Sentences
    ];

    private sealed class LoggedDeletion
    {
        public string RowId { get; set; } = string.Empty;
        public string? OwnerId { get; set; }
    }

    private static LearningResource SmartList(string id, string owner, string type, int minutesAgo) => new()
    {
        Id = id,
        UserProfileId = owner,
        Title = type,
        MediaType = "Smart Vocabulary List",
        Language = "Korean",
        IsSmartResource = true,
        SmartResourceType = type,
        CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
        UpdatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };

    private static Task SeedAsync(ServiceProvider app, string learner) =>
        Task.Run(() => app.GetRequiredService<SmartResourceService>().InitializeSmartResourcesAsync("Korean", learner));

    private static async Task AssertEachListOnceAsync(ApplicationOperationPostgresHarness database, string learner)
    {
        await using var db = database.NewContext();
        var types = await db.LearningResources.AsNoTracking()
            .Where(r => r.UserProfileId == learner && r.IsSmartResource)
            .Select(r => r.SmartResourceType!)
            .ToListAsync();

        types.Should().BeEquivalentTo(
        [
            SmartResourceService.SmartResourceType_DailyReview,
            SmartResourceService.SmartResourceType_NewWords,
            SmartResourceService.SmartResourceType_Struggling,
            SmartResourceService.SmartResourceType_Phrases,
            SmartResourceService.SmartResourceType_Sentences
        ], $"each smart list is made once for {learner}");
    }

    private static async Task<List<string>> ListIdsAsync(ApplicationOperationPostgresHarness database, string learner)
    {
        await using var db = database.NewContext();
        return await db.LearningResources.AsNoTracking()
            .Where(r => r.UserProfileId == learner && r.IsSmartResource)
            .Select(r => r.Id)
            .ToListAsync();
    }

    /// <summary>One app instance: the WebApp's registration, a retrying PostgreSQL context.</summary>
    private static ServiceProvider App(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 2, maxRetryDelay: TimeSpan.FromMilliseconds(50), errorCodesToAdd: null))
            .ConfigureWarnings(ApplicationOperationPostgresHarness.IgnoreKnownLegacyTimestampDrift));
        services.AddSingleton<IFileSystemService, StubFileSystemService>();
        services.AddSingleton<ISyncService, NoOpSyncService>();
        // As the app registers them (CoreServiceExtensions): singletons that open a scope per call.
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<SmartResourceService>();
        services.AddScoped<SentenceStudio.Services.Plans.IPlanDateContext>(
            _ => new SentenceStudio.Services.Plans.PlanDateContext(TimeZoneInfo.Utc));
        return services.BuildServiceProvider();
    }

    private sealed class StubFileSystemService : IFileSystemService
    {
        public string AppDataDirectory => AppContext.BaseDirectory;

        public Task<Stream> OpenAppPackageFileAsync(string filename) => Task.FromResult<Stream>(new MemoryStream());
    }
}
