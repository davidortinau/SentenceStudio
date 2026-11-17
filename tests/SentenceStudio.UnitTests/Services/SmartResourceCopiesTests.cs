using Xunit;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// One set of smart lists per learner (Captain, 2026-11-16), on a device's SQLite database: the
/// lists get the same IDs in every database, a device's seed leaves stored copies to the server,
/// and the fold (which the server runs) moves what named a copy to the kept list.
/// </summary>
/// <remarks>
/// The MAUI app seeds its own database, and a seed there before the device's first sync made a
/// second set with new IDs that sync uploaded beside the server's. The server side (the seed
/// folding copies, which copy is kept, the kept list refilled, the delete reaching other devices)
/// is in <c>SentenceStudio.Api.Tests/Data/SmartResourceSeedingPostgresTests</c>.
/// </remarks>
public sealed class SmartResourceCopiesTests : IDisposable
{
    private const string Learner = "copies-learner-a";
    private const string OtherLearner = "copies-learner-b";

    private static readonly string[] AllTypes =
    [
        SmartResourceService.SmartResourceType_DailyReview,
        SmartResourceService.SmartResourceType_NewWords,
        SmartResourceService.SmartResourceType_Struggling,
        SmartResourceService.SmartResourceType_Phrases,
        SmartResourceService.SmartResourceType_Sentences
    ];

    private readonly List<(SqliteConnection Connection, ServiceProvider Services)> _databases = new();

    public void Dispose()
    {
        foreach (var (connection, services) in _databases)
        {
            services.Dispose();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task SeedsInTwoDatabases_MakeTheSameIds()
    {
        var server = NewDatabase();
        var phone = NewDatabase();

        await SeedAsync(server, Learner);
        await SeedAsync(phone, Learner);

        var serverIds = await ListsAsync(server, Learner);
        var phoneIds = await ListsAsync(phone, Learner);
        phoneIds.Should().BeEquivalentTo(serverIds, "a list made on a phone before its first sync is the same row as the server's");
        serverIds.Select(list => list.Id).Should().BeEquivalentTo(
            AllTypes.Select(type => SyncIds.SmartResource(Learner, type)));
    }

    [Fact]
    public void TheFixedId_DependsOnLearnerAndType_AndHidesTheLearner()
    {
        var id = SyncIds.SmartResource(Learner, SmartResourceService.SmartResourceType_DailyReview);

        id.Should().MatchRegex("^sr:[0-9a-f]{32}$");
        id.Should().NotContain(Learner, "resource IDs travel in URLs, which the server logs");
        SyncIds.SmartResource(Learner, SmartResourceService.SmartResourceType_DailyReview).Should().Be(id);
        SyncIds.SmartResource(OtherLearner, SmartResourceService.SmartResourceType_DailyReview).Should().NotBe(id);
        SyncIds.SmartResource(Learner, SmartResourceService.SmartResourceType_NewWords).Should().NotBe(id);
    }

    /// <summary>The vectors in docs/sync-all-tables-plan.md, "Smart resources", for other clients.</summary>
    [Theory]
    [InlineData("owner-1", "DailyReview", "sr:c4e8cc8fdaca586ec9375d2b3727ebce")]
    [InlineData("owner-1", "Phrases", "sr:2c9d87bc49541f1fb584a09fc0cda6cb")]
    public void TheFixedId_MatchesTheDocumentedFormat(string owner, string type, string expected) =>
        SyncIds.SmartResource(owner, type).Should().Be(expected);

    [Fact]
    public async Task Folding_MovesWhatNamedACopy_AndDeletesTheCopies()
    {
        var database = NewDatabase();
        await using (var db = Context(database))
        {
            db.UserProfiles.Add(new UserProfile { Id = Learner, Name = "Learner", TargetLanguage = "Korean", NativeLanguage = "English" });
            db.LearningResources.AddRange(
                SmartList("kept-review", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60),
                SmartList("copy-review", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 10),
                SmartList("other-copy-review", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 5));
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "word-1", UserProfileId = Learner, TargetLanguageTerm = "사과", NativeLanguageTerm = "apple",
                Language = "Korean", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping { Id = "map-1", ResourceId = "copy-review", VocabularyWordId = "word-1" });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = "progress-1", VocabularyWordId = "word-1", UserId = Learner, TotalAttempts = 3, CorrectAttempts = 2,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = "context-1", VocabularyProgressId = "progress-1", LearningResourceId = "copy-review", Activity = "VocabularyQuiz"
            });
            db.VideoImports.Add(new VideoImport { Id = "video-1", UserProfileId = Learner, LearningResourceId = "other-copy-review" });
            // Saved before its table had an owner; it names the learner's copy, so it moves too.
            db.VideoImports.Add(new VideoImport { Id = "video-legacy", UserProfileId = null, LearningResourceId = "copy-review" });
            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = "plan-item-1", UserProfileId = Learner, Date = DateTime.UtcNow.Date, PlanItemId = "item-1",
                ActivityType = "VocabularyReview", ResourceId = "copy-review", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            db.VocabularyLookups.Add(new VocabularyLookup
            {
                Id = "lookup-1", UserProfileId = Learner, Text = "사과", Translation = "apple", Language = "Korean",
                Activity = "Reading", ResourceId = "copy-review", FirstLookedUpAt = DateTime.UtcNow, LastLookedUpAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var removed = await database.Services.GetRequiredService<LearningResourceRepository>()
            .MergeSmartResourceCopiesAsync("kept-review", ["copy-review", "other-copy-review"], Learner);

        removed.Should().Be(2);
        (await ListsAsync(database, Learner)).Select(list => list.Id).Should().Equal(["kept-review"]);
        await using var check = Context(database);
        (await check.VocabularyLearningContexts.SingleAsync(c => c.Id == "context-1")).LearningResourceId.Should().Be("kept-review");
        (await check.VideoImports.SingleAsync(v => v.Id == "video-1")).LearningResourceId.Should().Be("kept-review");
        (await check.VideoImports.SingleAsync(v => v.Id == "video-legacy")).LearningResourceId.Should().Be("kept-review");
        (await check.VocabularyLookups.SingleAsync(l => l.Id == "lookup-1")).ResourceId.Should().Be("kept-review");
        (await check.DailyPlanCompletions.SingleAsync(c => c.Id == "plan-item-1")).ResourceId
            .Should().Be("copy-review", "a plan item's ID is a hash of its resource, so it is left as it is");
        (await check.VocabularyWords.AnyAsync(w => w.Id == "word-1")).Should().BeTrue("the learner's word stays");
        (await check.VocabularyProgresses.AnyAsync(p => p.Id == "progress-1")).Should().BeTrue("their progress stays");
        (await check.ResourceVocabularyMappings.AnyAsync(m => m.ResourceId == "copy-review")).Should().BeFalse("a deleted copy's links go with it");
    }

    [Fact]
    public async Task Folding_ACopyAlreadyGone_StillFoldsTheRest()
    {
        var database = NewDatabase();
        await using (var db = Context(database))
        {
            db.LearningResources.AddRange(
                SmartList("kept-review", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60),
                SmartList("copy-review", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 10));
            await db.SaveChangesAsync();
        }

        var removed = await database.Services.GetRequiredService<LearningResourceRepository>()
            .MergeSmartResourceCopiesAsync("kept-review", ["deleted-by-a-sync", "copy-review"], Learner);

        removed.Should().Be(1);
        (await ListsAsync(database, Learner)).Select(list => list.Id).Should().Equal(["kept-review"]);
    }

    /// <summary>
    /// A device holds only what it has synced, so folding there could delete the server's lists
    /// while rows it has not downloaded still name them. It leaves the copies to the server.
    /// </summary>
    [Fact]
    public async Task ADeviceSeed_LeavesStoredCopiesToTheServer()
    {
        var database = NewDatabase();
        await using (var db = Context(database))
        {
            db.LearningResources.AddRange(
                SmartList("review-1", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60),
                SmartList("review-2", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 10));
            await db.SaveChangesAsync();
        }

        await SeedAsync(database, Learner);

        var lists = await ListsAsync(database, Learner);
        lists.Where(list => list.Type == SmartResourceService.SmartResourceType_DailyReview).Select(list => list.Id)
            .Should().BeEquivalentTo(["review-1", "review-2"]);
        lists.Should().HaveCount(6, "the four missing types are made once each, and Daily Review is not made again");
    }

    [Fact]
    public async Task Folding_RefusesAnotherLearnersCopy()
    {
        var database = NewDatabase();
        await using (var db = Context(database))
        {
            db.LearningResources.AddRange(
                SmartList("a-review", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60),
                SmartList("b-review", OtherLearner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 30));
            await db.SaveChangesAsync();
        }

        var removed = await database.Services.GetRequiredService<LearningResourceRepository>()
            .MergeSmartResourceCopiesAsync("a-review", ["b-review"], Learner);

        removed.Should().Be(0);
        (await ListsAsync(database, OtherLearner)).Should().ContainSingle(list => list.Id == "b-review");
    }

    [Fact]
    public async Task Folding_RefusesACopyOfAnotherType()
    {
        var database = NewDatabase();
        await using (var db = Context(database))
        {
            db.LearningResources.AddRange(
                SmartList("a-review", Learner, SmartResourceService.SmartResourceType_DailyReview, minutesAgo: 60),
                SmartList("a-phrases", Learner, SmartResourceService.SmartResourceType_Phrases, minutesAgo: 30));
            await db.SaveChangesAsync();
        }

        var removed = await database.Services.GetRequiredService<LearningResourceRepository>()
            .MergeSmartResourceCopiesAsync("a-review", ["a-phrases"], Learner);

        removed.Should().Be(0);
        (await ListsAsync(database, Learner)).Should().HaveCount(2);
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

    private static Task SeedAsync((SqliteConnection Connection, ServiceProvider Services) database, string learner) =>
        database.Services.GetRequiredService<SmartResourceService>().InitializeSmartResourcesAsync("Korean", learner);

    private static async Task<List<(string Id, string Type)>> ListsAsync(
        (SqliteConnection Connection, ServiceProvider Services) database, string learner)
    {
        await using var db = Context(database);
        var rows = await db.LearningResources.AsNoTracking()
            .Where(r => r.UserProfileId == learner && r.IsSmartResource)
            .Select(r => new { r.Id, r.SmartResourceType })
            .ToListAsync();
        return rows.Select(r => (r.Id, r.SmartResourceType!)).ToList();
    }

    private static ApplicationDbContext Context((SqliteConnection Connection, ServiceProvider Services) database) =>
        database.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();

    /// <summary>One device database, with the services registered as the apps register them.</summary>
    private (SqliteConnection Connection, ServiceProvider Services) NewDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton(Mock.Of<IPreferencesService>());
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<SmartResourceService>();
        services.AddScoped<SentenceStudio.Services.Plans.IPlanDateContext>(
            _ => new SentenceStudio.Services.Plans.PlanDateContext(TimeZoneInfo.Utc));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();

        _databases.Add((connection, provider));
        return (connection, provider);
    }
}
