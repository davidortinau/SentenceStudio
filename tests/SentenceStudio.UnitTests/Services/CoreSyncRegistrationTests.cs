using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public class CoreSyncRegistrationTests
{
    [Fact]
    public async Task CoreSyncRoundTrip_SynchronizesDailyPlanFocusFactsAndCompletions()
    {
        var localStore = await CreateProvisionedStoreAsync($"CoreSyncLocal{Guid.NewGuid():N}", ProviderMode.Local);
        var remoteStore = await CreateProvisionedStoreAsync($"CoreSyncRemote{Guid.NewGuid():N}", ProviderMode.Remote);
        await using var localKeepAlive = localStore.KeepAlive;
        await using var remoteKeepAlive = remoteStore.KeepAlive;

        var userId = "user-sync";
        var date = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);
        var focusFacts = "{\"vocabularyIds\":[\"word-1\",\"word-2\"],\"source\":\"deterministic-srs\"}";

        await using (var db = new ApplicationDbContext(localStore.Options))
        {
            db.DailyPlans.Add(new DailyPlan
            {
                Id = "plan-sync",
                UserProfileId = userId,
                Date = date,
                GeneratedAtUtc = date.AddHours(8),
                Strategy = "deterministic",
                FocusVocabularyFacts = focusFacts,
                CreatedAt = date.AddHours(8),
                UpdatedAt = date.AddHours(8)
            });
            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = "completion-sync",
                UserProfileId = userId,
                Date = date,
                PlanItemId = "item-sync",
                ActivityType = "VocabularyReview",
                IsCompleted = true,
                CompletedAt = date.AddHours(9),
                MinutesSpent = 7,
                EstimatedMinutes = 10,
                Priority = 1,
                TitleKey = "plan_item_vocab_review_title",
                DescriptionKey = "plan_item_vocab_review_desc",
                Rationale = string.Empty,
                CreatedAt = date.AddHours(8),
                UpdatedAt = date.AddHours(9)
            });
            await db.SaveChangesAsync();
        }

        var agent = new SyncAgent(localStore.Provider, remoteStore.Provider);
        await agent.SynchronizeAsync();

        await using (var db = new ApplicationDbContext(remoteStore.Options))
        {
            var remotePlan = await db.DailyPlans.SingleAsync(p => p.UserProfileId == userId && p.Date == date);
            Assert.Equal(focusFacts, remotePlan.FocusVocabularyFacts);

            var remoteCompletion = await db.DailyPlanCompletions.SingleAsync(c => c.UserProfileId == userId
                && c.Date == date
                && c.PlanItemId == "item-sync");
            Assert.True(remoteCompletion.IsCompleted);
            Assert.Equal(7, remoteCompletion.MinutesSpent);
        }
    }

    [Fact]
    public async Task CoreSyncRegistration_IncludesDailyPlan_WithFocusVocabularyFactsColumn()
    {
        const string connectionString = "Data Source=CoreSyncDailyPlanRegistration;Mode=Memory;Cache=Shared";

        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(keepAlive)
            .Options;

        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var configuration = new SqliteSyncConfigurationBuilder(connectionString)
            .ConfigureSyncTables()
            .Build();
        Assert.NotNull(typeof(DailyPlan).GetProperty(nameof(DailyPlan.FocusVocabularyFacts)));

        var provider = new SqliteSyncProvider(configuration, ProviderMode.Remote);
        await provider.ApplyProvisionAsync();

        var syncObjects = await GetSqliteObjectNamesAsync(keepAlive, "%DailyPlan%");
        Assert.Contains(syncObjects, name => !string.Equals(name, "DailyPlan", StringComparison.Ordinal)
            && name.Contains("DailyPlan", StringComparison.Ordinal)
            && !name.Contains("DailyPlanCompletion", StringComparison.Ordinal));

        var dailyPlanColumns = await GetColumnNamesAsync(keepAlive, "DailyPlan");
        Assert.Contains(nameof(DailyPlan.FocusVocabularyFacts), dailyPlanColumns);

        var completionColumns = await GetColumnNamesAsync(keepAlive, "DailyPlanCompletion");
        Assert.DoesNotContain("FocusVocabularyIdsJson", completionColumns);
    }

    [Fact]
    public async Task ResourceMetadataUpdate_IsTrackedForNextCoreSyncRoundTrip()
    {
        var local = await CreateProvisionedStoreAsync($"MetadataLocal{Guid.NewGuid():N}", ProviderMode.Local);
        var remote = await CreateProvisionedStoreAsync($"MetadataRemote{Guid.NewGuid():N}", ProviderMode.Remote);
        await using var localKeepAlive = local.KeepAlive;
        await using var remoteKeepAlive = remote.KeepAlive;

        var timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = new ApplicationDbContext(local.Options))
        {
            db.LearningResources.AddRange(
                new LearningResource
                {
                    Id = "owned-metadata",
                    UserProfileId = "owner-a",
                    Title = "Before",
                    Description = "Original",
                    Tags = "old, tags",
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                },
                new LearningResource
                {
                    Id = "foreign-metadata",
                    UserProfileId = "owner-b",
                    Title = "Other owner",
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                });
            await db.SaveChangesAsync();
        }

        await new SyncAgent(local.Provider, remote.Provider).SynchronizeAsync();
        await using (var db = new ApplicationDbContext(remote.Options))
        {
            Assert.Equal("Before", (await db.LearningResources.SingleAsync(r => r.Id == "owned-metadata")).Title);
        }

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(localKeepAlive));
        using var serviceProvider = services.BuildServiceProvider();
        var repository = new LearningResourceRepository(
            serviceProvider,
            NullLogger<LearningResourceRepository>.Instance,
            Mock.Of<IFileSystemService>());

        ResourceMetadataSnapshot original;
        await using (var db = new ApplicationDbContext(local.Options))
        {
            original = await db.LearningResources.AsNoTracking()
                .Where(r => r.Id == "owned-metadata" && r.UserProfileId == "owner-a")
                .Select(r => new ResourceMetadataSnapshot(
                    r.UpdatedAt, r.Title, r.Description, r.Tags, r.UpdatedAt.ToString()))
                .SingleAsync();
        }

        await using (var db = new ApplicationDbContext(remote.Options))
        {
            var row = await db.LearningResources.SingleAsync(r => r.Id == "owned-metadata");
            row.Description = "Remote description";
            row.Tags = "remote, tags";
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await new SyncAgent(local.Provider, remote.Provider).SynchronizeAsync();
        Assert.Equal(ResourceMetadataUpdateResult.Conflict,
            await repository.UpdateResourceMetadataAsync(
                "owned-metadata", "owner-a", original, "Stale title", "Original", "old, tags"));
        await using (var db = new ApplicationDbContext(local.Options))
        {
            var row = await db.LearningResources.AsNoTracking()
                .SingleAsync(r => r.Id == "owned-metadata");
            Assert.Equal("Before", row.Title);
            Assert.Equal("Remote description", row.Description);
            Assert.Equal("remote, tags", row.Tags);
            original = await db.LearningResources.AsNoTracking()
                .Where(r => r.Id == "owned-metadata" && r.UserProfileId == "owner-a")
                .Select(r => new ResourceMetadataSnapshot(
                    r.UpdatedAt, r.Title, r.Description, r.Tags, r.UpdatedAt.ToString()))
                .SingleAsync();
        }

        Assert.Equal(ResourceMetadataUpdateResult.Success,
            await repository.UpdateResourceMetadataAsync(
                "owned-metadata", "owner-a", original, "After", "Updated", "new,  spaced"));
        await using (var db = new ApplicationDbContext(remote.Options))
        {
            Assert.Equal("Before", (await db.LearningResources.SingleAsync(r => r.Id == "owned-metadata")).Title);
            Assert.Equal("Remote description", (await db.LearningResources.SingleAsync(r => r.Id == "owned-metadata")).Description);
        }

        await new SyncAgent(local.Provider, remote.Provider).SynchronizeAsync();
        await using (var db = new ApplicationDbContext(remote.Options))
        {
            var updated = await db.LearningResources.SingleAsync(r => r.Id == "owned-metadata");
            Assert.Equal("owner-a", updated.UserProfileId);
            Assert.Equal("After", updated.Title);
            Assert.Equal("Updated", updated.Description);
            Assert.Equal("new,  spaced", updated.Tags);
            Assert.True(updated.UpdatedAt > timestamp);

            var foreign = await db.LearningResources.SingleAsync(r => r.Id == "foreign-metadata");
            Assert.Equal("owner-b", foreign.UserProfileId);
            Assert.Equal("Other owner", foreign.Title);
            Assert.Equal(timestamp, foreign.UpdatedAt);
        }
    }

    private static async Task<ProvisionedStore> CreateProvisionedStoreAsync(string storeName, ProviderMode mode)
    {
        var connectionString = $"Data Source={storeName};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(keepAlive)
            .Options;

        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var configuration = new SqliteSyncConfigurationBuilder(connectionString)
            .ConfigureSyncTables()
            .Build();
        var provider = new SqliteSyncProvider(configuration, mode);
        await provider.ApplyProvisionAsync();

        return new ProvisionedStore(keepAlive, options, provider);
    }

    private sealed record ProvisionedStore(
        SqliteConnection KeepAlive,
        DbContextOptions<ApplicationDbContext> Options,
        ISyncProvider Provider);

    private static async Task<HashSet<string>> GetSqliteObjectNamesAsync(SqliteConnection connection, string namePattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE name LIKE $pattern;";
        command.Parameters.AddWithValue("$pattern", namePattern);

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
