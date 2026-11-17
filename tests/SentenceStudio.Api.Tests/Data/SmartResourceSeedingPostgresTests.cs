using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Data;
using SentenceStudio.Services;

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
