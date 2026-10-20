using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.Data;

public sealed class CrossProfileRepositoryBoundaryTests : IDisposable
{
    private const string UserA = "boundary-user-a";
    private const string UserB = "boundary-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly SelectCommandCounter _queryCounter;
    private readonly CollectingLoggerProvider _logs;
    private string _activeUserId = UserA;

    public CrossProfileRepositoryBoundaryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _queryCounter = new SelectCommandCounter();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(_connection)
                .AddInterceptors(_queryCounter)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>()))
            .Returns(() => _activeUserId);
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());

        _logs = new CollectingLoggerProvider();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_logs);
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        services.AddSingleton(preferences.Object);
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.AddRange(CreateUser(UserA), CreateUser(UserB));
        db.SaveChanges();
        _queryCounter.Reset();
    }

    [Fact]
    public async Task ExactReads_EmptyUserFailClosedBeforeAnyQuery()
    {
        var resource = SeedResource(UserA);
        var skill = SeedSkill(UserA);
        _activeUserId = string.Empty;
        _queryCounter.Reset();

        var resourceResult = await ResourceRepository.GetResourceAsync(resource.Id);
        var skillResult = await SkillRepository.GetSkillProfileAsync(skill.Id);
        var skillAliasResult = await SkillRepository.GetAsync(skill.Id);

        resourceResult.Should().BeNull();
        skillResult.Should().BeNull();
        skillAliasResult.Should().BeNull();
        _queryCounter.SelectCount.Should().Be(0, "empty-user exact reads must not reach the database");
        _logs.HasWarningContaining("GetResourceAsync", "cross-tenant").Should().BeTrue();
        _logs.HasWarningContaining("GetSkillProfileAsync", "cross-tenant").Should().BeTrue();
        _logs.HasWarningContaining("GetAsync", "cross-tenant").Should().BeTrue();
    }

    [Fact]
    public async Task ExactReads_OnlyReturnEntitiesOwnedByExplicitUser()
    {
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var skillA = SeedSkill(UserA);
        var skillB = SeedSkill(UserB);

        (await ResourceRepository.GetResourceAsync(resourceA.Id, UserA)).Should().NotBeNull();
        (await ResourceRepository.GetResourceAsync(resourceB.Id, UserA)).Should().BeNull();
        (await ResourceRepository.GetResourceAsync("missing-resource", UserA)).Should().BeNull();

        (await SkillRepository.GetSkillProfileAsync(skillA.Id, UserA)).Should().NotBeNull();
        (await SkillRepository.GetSkillProfileAsync(skillB.Id, UserA)).Should().BeNull();
        (await SkillRepository.GetAsync(skillA.Id, UserA)).Should().NotBeNull();
        (await SkillRepository.GetAsync(skillB.Id, UserA)).Should().BeNull();
        (await SkillRepository.GetAsync("missing-skill", UserA)).Should().BeNull();
    }

    [Fact]
    public async Task ResourceWrites_EmptyAndForeignUserRefuseWithoutMutation()
    {
        var owned = SeedResource(UserA, title: "Owned");
        var foreign = SeedResource(UserB, title: "Foreign");

        foreign.Title = "Attempted foreign update";
        (await ResourceRepository.SaveResourceAsync(foreign, UserA)).Should().BeEmpty();
        (await ResourceRepository.DeleteResourceAsync(foreign, UserA)).Should().Be(0);

        _activeUserId = string.Empty;
        _queryCounter.Reset();
        var unsaved = new LearningResource { Title = "No owner" };
        (await ResourceRepository.SaveResourceAsync(unsaved)).Should().BeEmpty();
        (await ResourceRepository.DeleteResourceAsync(owned)).Should().Be(0);
        _queryCounter.SelectCount.Should().Be(0, "empty-user writes and deletes must refuse before querying");

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.LearningResources.AsNoTracking().SingleAsync(r => r.Id == foreign.Id))
            .Title.Should().Be("Foreign");
        (await db.LearningResources.AsNoTracking().AnyAsync(r => r.Id == owned.Id)).Should().BeTrue();
        (await db.LearningResources.AsNoTracking().AnyAsync(r => r.Id == unsaved.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ResourceWrites_OwnedUpdateCreateAndDeleteStayWithinOwner()
    {
        var owned = SeedResource(UserA, title: "Before");
        owned.Title = "After";
        owned.UserProfileId = UserB;

        (await ResourceRepository.SaveResourceAsync(owned, UserA)).Should().Be(owned.Id);
        var created = new LearningResource { Title = "Created", UserProfileId = UserB };
        (await ResourceRepository.SaveResourceAsync(created, UserA)).Should().Be(created.Id);
        (await ResourceRepository.DeleteResourceAsync(owned, UserA)).Should().BeGreaterThan(0);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedCreated = await db.LearningResources.AsNoTracking().SingleAsync(r => r.Id == created.Id);
        persistedCreated.UserProfileId.Should().Be(UserA);
        (await db.LearningResources.AsNoTracking().AnyAsync(r => r.Id == owned.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ResourceMetadataUpdate_ChangesOnlyOwnedMetadataAndPreservesAssociatedData()
    {
        var owned = SeedResource(UserA, title: "Before");
        var foreign = SeedResource(UserB, title: "Foreign");
        var word = SeedWord("source word");
        Map(owned.Id, word.Id);
        Map(foreign.Id, word.Id);

        var originalTimestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        const string transcript = "Line 1\r\nLine 2: 안녕하세요";
        const string translation = "Line 1\r\nLine 2: hello";
        using (var seedScope = _provider.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.SingleAsync(r => r.Id == owned.Id);
            row.Description = "Old description";
            row.Tags = "old,  spaced";
            row.MediaType = "Video";
            row.MediaUrl = "https://example.test/source";
            row.Transcript = transcript;
            row.Translation = translation;
            row.SkillID = "source-skill";
            row.OldVocabularyListID = "source-list";
            row.CreatedAt = originalTimestamp;
            row.UpdatedAt = originalTimestamp;
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                VocabularyWordId = word.Id,
                UserId = UserA,
                MasteryScore = 0.5f,
                TotalAttempts = 3,
                CreatedAt = originalTimestamp,
                UpdatedAt = originalTimestamp
            });
            db.DailyPlans.Add(new DailyPlan
            {
                Id = "metadata-plan",
                UserProfileId = UserA,
                Date = originalTimestamp.Date,
                GeneratedAtUtc = originalTimestamp,
                Strategy = "deterministic",
                FocusVocabularyFacts = "{\"vocabularyIds\":[\"source-word\"]}",
                CreatedAt = originalTimestamp,
                UpdatedAt = originalTimestamp
            });
            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = "metadata-plan-item",
                UserProfileId = UserA,
                Date = originalTimestamp.Date,
                PlanItemId = "metadata-item",
                ActivityType = "VocabularyReview",
                ResourceId = owned.Id,
                CreatedAt = originalTimestamp,
                UpdatedAt = originalTimestamp
            });
            db.UserActivities.Add(new UserActivity
            {
                Id = "metadata-activity",
                UserProfileId = UserA,
                Activity = "Reading",
                Input = "Original answer",
                Accuracy = 75,
                CreatedAt = originalTimestamp,
                UpdatedAt = originalTimestamp
            });
            db.ActivitySessions.Add(new ActivitySession
            {
                UserId = UserA,
                ActivityType = "VocabularyReview",
                LaunchContextKey = "metadata-session",
                StateJson = "{}",
                StartedAt = originalTimestamp,
                UpdatedAt = originalTimestamp
            });
            await db.SaveChangesAsync();
        }

        using var inspectScope = _provider.CreateScope();
        var inspect = inspectScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == owned.Id);
        var foreignBefore = await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == foreign.Id);
        var mappingsBefore = await inspect.ResourceVocabularyMappings.AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.ResourceId, m.VocabularyWordId })
            .ToArrayAsync();
        var wordBefore = await inspect.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == word.Id);
        var progressBefore = await inspect.VocabularyProgresses.AsNoTracking()
            .SingleAsync(p => p.VocabularyWordId == word.Id);
        var planBefore = await inspect.DailyPlans.AsNoTracking().SingleAsync(p => p.Id == "metadata-plan");
        var planItemBefore = await inspect.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(p => p.Id == "metadata-plan-item");
        var activityBefore = await inspect.UserActivities.AsNoTracking()
            .SingleAsync(a => a.Id == "metadata-activity");
        var sessionBefore = await inspect.ActivitySessions.AsNoTracking()
            .SingleAsync(s => s.LaunchContextKey == "metadata-session");

        var original = await SnapshotAsync(owned.Id, UserA);
        _queryCounter.Reset();
        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, original, "After", "New description", "new,  spaced"))
            .Should().Be(ResourceMetadataUpdateResult.Success);
        _queryCounter.UpdateCount.Should().Be(1, "metadata changes must be one conditional write");
        _queryCounter.SelectCount.Should().Be(0, "a successful compare-and-swap needs no preliminary read");
        var predicate = _queryCounter.LastUpdateCommand!.Split("WHERE", 2)[1];
        predicate.Should().Contain("\"Id\"").And.Contain("\"UserProfileId\"")
            .And.Contain("\"IsSmartResource\"").And.Contain("\"Title\"")
            .And.Contain("\"Description\"").And.Contain("\"Tags\"")
            .And.Contain("\"UpdatedAt\"").And.Contain("CAST(");

        var after = await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == owned.Id);
        after.Title.Should().Be("After");
        after.Description.Should().Be("New description");
        after.Tags.Should().Be("new,  spaced", "resource tags are stored as the source's comma-separated text");
        after.Id.Should().Be(before.Id);
        after.UserProfileId.Should().Be(UserA);
        after.MediaType.Should().Be(before.MediaType);
        after.Language.Should().Be(before.Language);
        after.MediaUrl.Should().Be(before.MediaUrl);
        after.Transcript.Should().Be(before.Transcript);
        after.Translation.Should().Be(before.Translation);
        after.SkillID.Should().Be(before.SkillID);
        after.OldVocabularyListID.Should().Be(before.OldVocabularyListID);
        after.IsSmartResource.Should().Be(before.IsSmartResource);
        after.SmartResourceType.Should().Be(before.SmartResourceType);
        after.CreatedAt.Should().Be(before.CreatedAt);
        after.UpdatedAt.Should().BeAfter(before.UpdatedAt);
        System.Text.Encoding.UTF8.GetBytes(after.Transcript!).Should()
            .Equal(System.Text.Encoding.UTF8.GetBytes(transcript));
        System.Text.Encoding.UTF8.GetBytes(after.Translation!).Should()
            .Equal(System.Text.Encoding.UTF8.GetBytes(translation));

        var mappingsAfter = await inspect.ResourceVocabularyMappings.AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.ResourceId, m.VocabularyWordId })
            .ToArrayAsync();
        mappingsAfter.Should().Equal(mappingsBefore);
        (await inspect.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == word.Id))
            .Should().BeEquivalentTo(wordBefore);
        var progressAfter = await inspect.VocabularyProgresses.AsNoTracking()
            .SingleAsync(p => p.VocabularyWordId == word.Id);
        progressAfter.Should().BeEquivalentTo(progressBefore);
        (await inspect.DailyPlans.AsNoTracking().SingleAsync(p => p.Id == "metadata-plan"))
            .Should().BeEquivalentTo(planBefore);
        (await inspect.DailyPlanCompletions.AsNoTracking().SingleAsync(p => p.Id == "metadata-plan-item"))
            .Should().BeEquivalentTo(planItemBefore);
        (await inspect.UserActivities.AsNoTracking().SingleAsync(a => a.Id == "metadata-activity"))
            .Should().BeEquivalentTo(activityBefore);
        (await inspect.ActivitySessions.AsNoTracking().SingleAsync(s => s.LaunchContextKey == "metadata-session"))
            .Should().BeEquivalentTo(sessionBefore);
        (await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == foreign.Id))
            .Should().BeEquivalentTo(foreignBefore);
    }

    [Fact]
    public async Task ResourceMetadataUpdate_RejectsForeignMissingOwnerlessSmartAndInvalidRequests()
    {
        var owned = SeedResource(UserA, title: "Owned");
        var foreign = SeedResource(UserB, title: "Foreign");
        var smart = SeedResource(UserA, title: "Smart");
        var ownerless = new LearningResource
        {
            Title = "Ownerless",
            Description = "Orphan description",
            Tags = "orphan",
            UserProfileId = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        using (var seedScope = _provider.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(r => r.Id == owned.Id)).Tags = "owned";
            (await db.LearningResources.SingleAsync(r => r.Id == foreign.Id)).Tags = "foreign";
            var smartRow = await db.LearningResources.SingleAsync(r => r.Id == smart.Id);
            smartRow.IsSmartResource = true;
            smartRow.Tags = "smart";
            db.LearningResources.Add(ownerless);
            await db.SaveChangesAsync();
        }

        var expected = await SnapshotAsync(owned.Id, UserA);
        _queryCounter.Reset();
        (await ResourceRepository.UpdateResourceMetadataAsync(owned.Id, "", expected, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.NotFoundOrForbidden);
        (await ResourceRepository.UpdateResourceMetadataAsync(owned.Id, "  ", expected, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.NotFoundOrForbidden);
        _queryCounter.SelectCount.Should().Be(0, "absent explicit owners must never query or fall back to active preferences");
        _logs.HasWarningContaining("UpdateResourceMetadataAsync", "without an owner").Should().BeTrue();

        (await ResourceRepository.UpdateResourceMetadataAsync(foreign.Id, UserA, expected, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.NotFoundOrForbidden);
        (await ResourceRepository.UpdateResourceMetadataAsync("missing", UserA, expected, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.NotFoundOrForbidden);
        (await ResourceRepository.UpdateResourceMetadataAsync(ownerless.Id, UserA, expected, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.NotFoundOrForbidden);
        (await ResourceRepository.UpdateResourceMetadataAsync(smart.Id, UserA, expected, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.SmartReadOnly);
        (await ResourceRepository.UpdateResourceMetadataAsync("", UserA, expected, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.ValidationFailed);
        (await ResourceRepository.UpdateResourceMetadataAsync(owned.Id, UserA, expected, "  ", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.ValidationFailed);
        (await ResourceRepository.UpdateResourceMetadataAsync(owned.Id, UserA, null, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.ValidationFailed);
        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, expected with { SqliteUpdatedAtText = null }, "Wrong", "Wrong", "wrong"))
            .Should().Be(ResourceMetadataUpdateResult.ValidationFailed);

        using var inspectScope = _provider.CreateScope();
        var inspect = inspectScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resources = await inspect.LearningResources.AsNoTracking().ToListAsync();
        resources.Should().HaveCount(4);
        resources.Single(r => r.Id == owned.Id).Title.Should().Be("Owned");
        resources.Single(r => r.Id == owned.Id).Tags.Should().Be("owned");
        resources.Single(r => r.Id == foreign.Id).Title.Should().Be("Foreign");
        resources.Single(r => r.Id == foreign.Id).Tags.Should().Be("foreign");
        resources.Single(r => r.Id == smart.Id).Title.Should().Be("Smart");
        resources.Single(r => r.Id == smart.Id).Tags.Should().Be("smart");
        resources.Single(r => r.Id == ownerless.Id).Title.Should().Be("Ownerless");
        resources.Single(r => r.Id == ownerless.Id).Tags.Should().Be("orphan");
        resources.Single(r => r.Id == ownerless.Id).UserProfileId.Should().BeNull();
    }

    [Fact]
    public async Task ResourceMetadataUpdate_PreservesNullableAndEmptyTextStorage()
    {
        var owned = SeedResource(UserA, title: "Before");
        var original = await SnapshotAsync(owned.Id, UserA);
        (await ResourceRepository.UpdateResourceMetadataAsync(owned.Id, UserA, original, "After", null, null))
            .Should().Be(ResourceMetadataUpdateResult.Success);

        using var inspectScope = _provider.CreateScope();
        var inspect = inspectScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cleared = await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == owned.Id);
        cleared.Description.Should().BeNull();
        cleared.Tags.Should().BeNull();

        var clearedSnapshot = await SnapshotAsync(owned.Id, UserA);
        (await ResourceRepository.UpdateResourceMetadataAsync(owned.Id, UserA, clearedSnapshot, "After", "", ""))
            .Should().Be(ResourceMetadataUpdateResult.Success);
        var blank = await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == owned.Id);
        blank.Description.Should().BeEmpty();
        blank.Tags.Should().BeEmpty();

        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, await SnapshotAsync(owned.Id, UserA), "After", "", ""))
            .Should().Be(ResourceMetadataUpdateResult.Success, "an unchanged same-owner edit is valid");
    }

    [Fact]
    public async Task ResourceMetadataUpdate_StaleTitleEditCannotOverwriteCoreSyncMetadata()
    {
        var owned = SeedResource(UserA, title: "Opened title");
        var original = await SnapshotAsync(owned.Id, UserA);

        using (var syncScope = _provider.CreateScope())
        {
            var db = syncScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.SingleAsync(r => r.Id == owned.Id);
            row.Description = "Downloaded description";
            row.Tags = "downloaded, tags";
            await db.SaveChangesAsync();
        }

        _queryCounter.Reset();
        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, original, "Stale title", "Old description", "old, tags"))
            .Should().Be(ResourceMetadataUpdateResult.Conflict,
                "original scalar values must catch a CoreSync update even if its timestamp did not advance");
        _queryCounter.UpdateCount.Should().Be(1);
        _queryCounter.SelectCount.Should().Be(1, "only a refused write needs an owner-scoped status read");

        using var inspectScope = _provider.CreateScope();
        var inspect = inspectScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var current = await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == owned.Id);
        current.Title.Should().Be("Opened title");
        current.Description.Should().Be("Downloaded description");
        current.Tags.Should().Be("downloaded, tags");
        current.UpdatedAt.Should().Be(original.UpdatedAt);
        current.UserProfileId.Should().Be(UserA);
    }

    [Fact]
    public async Task ResourceMetadataUpdate_RejectsChangedTimestampAtTickPrecision()
    {
        var owned = SeedResource(UserA, title: "Opened title");
        var original = await SnapshotAsync(owned.Id, UserA);
        using (var syncScope = _provider.CreateScope())
        {
            var db = syncScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.SingleAsync(r => r.Id == owned.Id);
            row.UpdatedAt = original.UpdatedAt.AddTicks(10);
            await db.SaveChangesAsync();
        }

        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, original, "Stale title", original.Description, original.Tags))
            .Should().Be(ResourceMetadataUpdateResult.Conflict);

        var current = await SnapshotAsync(owned.Id, UserA);
        current.UpdatedAt.Should().Be(original.UpdatedAt.AddTicks(10));
        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, current, "Fresh title", current.Description, current.Tags))
            .Should().Be(ResourceMetadataUpdateResult.Success);
        (await SnapshotAsync(owned.Id, UserA)).Title.Should().Be("Fresh title");
    }

    [Fact]
    public async Task ResourceMetadataUpdate_DistinguishesNullAndEmptyScalarsAtSameTimestamp()
    {
        var owned = SeedResource(UserA, title: "Title");
        var original = await SnapshotAsync(owned.Id, UserA);
        using (var syncScope = _provider.CreateScope())
        {
            var db = syncScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.SingleAsync(r => r.Id == owned.Id);
            row.Description = "";
            row.Tags = "";
            await db.SaveChangesAsync();
        }

        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, original, "Wrong", "Wrong", "Wrong"))
            .Should().Be(ResourceMetadataUpdateResult.Conflict);
        var blank = await SnapshotAsync(owned.Id, UserA);
        blank.Description.Should().BeEmpty();
        blank.Tags.Should().BeEmpty();
        blank.UpdatedAt.Should().Be(original.UpdatedAt);

        using (var syncScope = _provider.CreateScope())
        {
            var db = syncScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.SingleAsync(r => r.Id == owned.Id);
            row.Description = null;
            row.Tags = null;
            row.Title = null;
            await db.SaveChangesAsync();
        }

        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, blank, "Wrong", null, null))
            .Should().Be(ResourceMetadataUpdateResult.Conflict);
        var withNullTitle = await SnapshotAsync(owned.Id, UserA);
        withNullTitle.Title.Should().BeNull();
        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, withNullTitle, "Valid title", null, null))
            .Should().Be(ResourceMetadataUpdateResult.Success);
    }

    [Fact]
    public async Task ResourceMetadataUpdate_AdvancesFutureTimestampWithoutChangingOtherFields()
    {
        var owned = SeedResource(UserA, title: "Before");
        var future = DateTime.UtcNow.AddMinutes(5);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(r => r.Id == owned.Id)).UpdatedAt = future;
            await db.SaveChangesAsync();
        }

        var original = await SnapshotAsync(owned.Id, UserA);
        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, original, "After", null, null))
            .Should().Be(ResourceMetadataUpdateResult.Success);
        (await SnapshotAsync(owned.Id, UserA)).UpdatedAt.Should().BeAfter(original.UpdatedAt);
    }

    [Fact]
    public async Task ResourceMetadataUpdate_CancellationDoesNotWrite()
    {
        var owned = SeedResource(UserA, title: "Before");
        var original = await SnapshotAsync(owned.Id, UserA);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResourceRepository.UpdateResourceMetadataAsync(
                owned.Id, UserA, original, "Canceled", "Canceled", "Canceled",
                cancellation.Token));

        (await SnapshotAsync(owned.Id, UserA)).Should().Be(original);
    }

    [Fact]
    public async Task ResourceMetadataUpdate_DatabaseFailureIsNotReportedAsConflict()
    {
        var owned = SeedResource(UserA, title: "Before");
        var original = await SnapshotAsync(owned.Id, UserA);
        _queryCounter.ThrowOnUpdate = true;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ResourceRepository.UpdateResourceMetadataAsync(
                    owned.Id, UserA, original, "After", "After", "After"));
        }
        finally
        {
            _queryCounter.ThrowOnUpdate = false;
        }

        (await SnapshotAsync(owned.Id, UserA)).Should().Be(original);
    }

    [Theory]
    [InlineData("2026-09-30 12:30:00.123456")]
    [InlineData("2026-09-30T12:30:00.123456Z")]
    public async Task ResourceMetadataUpdate_RoundTripsPersistedSqliteTimestampFormats(string storedAt)
    {
        var owned = SeedResource(UserA, title: "Before");
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE "LearningResource" SET "UpdatedAt" = $updatedAt WHERE "Id" = $id
                """;
            command.Parameters.AddWithValue("$updatedAt", storedAt);
            command.Parameters.AddWithValue("$id", owned.Id);
            await command.ExecuteNonQueryAsync();
        }

        var original = await SnapshotAsync(owned.Id, UserA);
        original.SqliteUpdatedAtText.Should().Be(storedAt);
        (await ResourceRepository.UpdateResourceMetadataAsync(
            owned.Id, UserA, original, "After", original.Description, original.Tags))
            .Should().Be(ResourceMetadataUpdateResult.Success,
                "a timestamp read from an unchanged SQLite row must match its persisted value");
        (await SnapshotAsync(owned.Id, UserA)).Title.Should().Be("After");
    }

    [Fact]
    public async Task ResourceMetadataUpdate_LegacyBoolOverloadCannotOverwriteWithoutSnapshot()
    {
        var owned = SeedResource(UserA, title: "Before");
        var original = await SnapshotAsync(owned.Id, UserA);

#pragma warning disable CS0618
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResourceRepository.UpdateResourceMetadataAsync(
                owned.Id, UserA, "Unsafe", "Unsafe", "Unsafe"));
#pragma warning restore CS0618

        (await SnapshotAsync(owned.Id, UserA)).Should().Be(original);
    }

    [Fact]
    public void ResourceMetadataUpdate_SqliteAndPostgresTranslateExactNullPredicates()
    {
        using var sqliteScope = _provider.CreateScope();
        var sqlite = sqliteScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        using var postgres = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=translation_only;Username=unused")
                .Options);
        var timestamp = new DateTime(2026, 9, 30, 12, 30, 0, DateTimeKind.Utc)
            .AddTicks(1234);
        const string storedAt = "2026-09-30 12:30:00.0001234";

        foreach (var db in new[] { sqlite, postgres })
        {
            var nullSql = LearningResourceRepository.MatchOriginalMetadata(
                db, "resource", UserA, new(timestamp, null, null, null, storedAt))
                .ToQueryString();
            nullSql.Should().Contain("\"Id\"").And.Contain("\"UserProfileId\"")
                .And.Contain("\"IsSmartResource\"");
            nullSql.Should().Contain("\"Title\" IS NULL")
                .And.Contain("\"Description\" IS NULL").And.Contain("\"Tags\" IS NULL");
            var nullPredicate = nullSql.Split("WHERE", 2)[1];
            if (db.Database.IsSqlite())
                nullPredicate.Should().Contain("CAST(").And.Contain("\"UpdatedAt\"");
            else
                nullPredicate.Should().Contain("\"UpdatedAt\"");

            var emptySql = LearningResourceRepository.MatchOriginalMetadata(
                db, "resource", UserA, new(timestamp, "", "", "", storedAt))
                .ToQueryString();
            emptySql.Should().Contain("\"Title\" =")
                .And.Contain("\"Description\" =").And.Contain("\"Tags\" =");
            emptySql.Should().NotContain("\"Description\" IS NULL")
                .And.NotContain("\"Tags\" IS NULL");
        }
    }

    [Fact]
    public async Task ResourceMetadataUpdate_CancelWithoutCallingUpdateDoesNotWrite()
    {
        var owned = SeedResource(UserA, title: "Before");
        var editedLocally = await ResourceRepository.GetResourceAsync(owned.Id, UserA);
        editedLocally.Should().NotBeNull();
        editedLocally!.Title = "Discarded";
        editedLocally.Tags = "discarded";

        using var inspectScope = _provider.CreateScope();
        var inspect = inspectScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var after = await inspect.LearningResources.AsNoTracking().SingleAsync(r => r.Id == owned.Id);
        after.Title.Should().Be("Before");
        after.Tags.Should().BeNull();
        after.UpdatedAt.Should().Be(owned.UpdatedAt);
    }

    private async Task<ResourceMetadataSnapshot> SnapshotAsync(string resourceId, string owner)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LearningResources.AsNoTracking()
            .Where(resource => resource.Id == resourceId && resource.UserProfileId == owner)
            .Select(resource => new ResourceMetadataSnapshot(
                resource.UpdatedAt, resource.Title, resource.Description,
                resource.Tags, resource.UpdatedAt.ToString()))
            .SingleAsync();
    }

    [Fact]
    public async Task SkillWrites_EmptyForeignAndOwnedCasesFailClosed()
    {
        var owned = SeedSkill(UserA, title: "Before");
        var foreign = SeedSkill(UserB, title: "Foreign");

        foreign.Title = "Attempted foreign update";
        (await SkillRepository.SaveAsync(foreign, UserA)).Should().BeEmpty();
        (await SkillRepository.DeleteAsync(foreign, UserA)).Should().Be(0);

        owned.Title = "After";
        owned.UserProfileId = UserB;
        (await SkillRepository.SaveAsync(owned, UserA)).Should().Be(owned.Id);

        var created = new SkillProfile { Title = "Created", UserProfileId = UserB };
        (await SkillRepository.SaveAsync(created, UserA)).Should().Be(created.Id);

        _activeUserId = string.Empty;
        _queryCounter.Reset();
        (await SkillRepository.SaveAsync(new SkillProfile { Title = "No owner" })).Should().BeEmpty();
        (await SkillRepository.DeleteAsync(owned)).Should().Be(0);
        _queryCounter.SelectCount.Should().Be(0, "empty-user skill writes must refuse before querying");

        _activeUserId = UserA;
        (await SkillRepository.DeleteAsync(owned)).Should().BeGreaterThan(0);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedForeign = await db.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == foreign.Id);
        persistedForeign.Title.Should().Be("Foreign");
        (await db.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == created.Id))
            .UserProfileId.Should().Be(UserA);
        (await db.SkillProfiles.AsNoTracking().AnyAsync(s => s.Id == owned.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ResourceVocabularyWrites_RejectForeignAndMixedIdentifiersWithoutPartialMutation()
    {
        var target = SeedResource(UserA);
        var ownedSource = SeedResource(UserA);
        var foreignSource = SeedResource(UserB);
        var ownedWord = SeedWord();
        var foreignWord = SeedWord();
        Map(ownedSource.Id, ownedWord.Id);
        Map(foreignSource.Id, foreignWord.Id);
        var mixedResource = new LearningResource
        {
            Title = "Mixed resource",
            Vocabulary = new List<VocabularyWord> { ownedWord, foreignWord },
        };

        (await ResourceRepository.AddVocabularyToResourceAsync(target.Id, foreignWord.Id, UserA))
            .Should().BeFalse();
        (await ResourceRepository.SaveResourceAsync(mixedResource, UserA)).Should().BeEmpty();
        (await ResourceRepository.BulkAssociateWordsWithResourceAsync(
            target.Id,
            new List<string> { ownedWord.Id, foreignWord.Id },
            UserA)).Should().BeFalse();
        (await ResourceRepository.RemoveVocabularyFromResourceAsync(foreignSource.Id, foreignWord.Id, UserA))
            .Should().BeFalse();
        (await ResourceRepository.BulkRemoveWordsFromResourceAsync(
            foreignSource.Id,
            new List<string> { foreignWord.Id },
            UserA)).Should().BeFalse();
        (await ResourceRepository.GetVocabularyWordsByResourceAsync(foreignSource.Id, UserA))
            .Should().BeEmpty();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ResourceVocabularyMappings.AsNoTracking()
            .CountAsync(m => m.ResourceId == target.Id)).Should().Be(0);
        (await db.LearningResources.AsNoTracking()
            .CountAsync(r => r.Id == mixedResource.Id)).Should().Be(0);
        (await db.ResourceVocabularyMappings.AsNoTracking()
            .CountAsync(m => m.ResourceId == foreignSource.Id && m.VocabularyWordId == foreignWord.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task TargetTermLookup_ReusesOnlyReachableOrUnownedWords()
    {
        const string commonTerm = "공통";
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var wordA = SeedWord(commonTerm);
        Map(resourceA.Id, wordA.Id);

        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserA))
            ?.Id.Should().Be(wordA.Id);
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            .Should().BeNull("a foreign-only mapping is not reusable");

        var unowned = SeedWord(commonTerm);
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            ?.Id.Should().Be(unowned.Id);

        (await ResourceRepository.AddVocabularyToResourceAsync(resourceB.Id, unowned.Id, UserB))
            .Should().BeTrue();
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            ?.Id.Should().Be(unowned.Id);
    }

    [Fact]
    public async Task CommonTermCreation_ForSecondProfileCreatesDistinctReachableWord()
    {
        const string commonTerm = "사람";
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var foreignWord = SeedWord(commonTerm);
        Map(resourceA.Id, foreignWord.Id);

        var lookup = await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB);
        lookup.Should().BeNull();

        var newWord = new VocabularyWord
        {
            TargetLanguageTerm = commonTerm,
            NativeLanguageTerm = "person",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        // The creating learner owns the new word (and any memory aids on it).
        (await ResourceRepository.SaveWordAsync(newWord, UserB)).Should().BeGreaterThan(0);
        (await ResourceRepository.AddVocabularyToResourceAsync(resourceB.Id, newWord.Id, UserB))
            .Should().BeTrue();

        newWord.Id.Should().NotBe(foreignWord.Id);
        (await ResourceRepository.GetWordByTargetTermAsync(commonTerm, UserB))
            ?.Id.Should().Be(newWord.Id);
    }

    [Fact]
    public async Task VocabularyGraph_ContainsOnlyExplicitUsersResources()
    {
        var resourceA = SeedResource(UserA);
        var resourceB = SeedResource(UserB);
        var sharedWord = SeedWord();
        Map(resourceA.Id, sharedWord.Id);
        Map(resourceB.Id, sharedWord.Id);

        var words = await ResourceRepository.GetAllVocabularyWordsWithResourcesAsync(UserA);

        words.Should().ContainSingle(word => word.Id == sharedWord.Id);
        var returned = words.Single(word => word.Id == sharedWord.Id);
        returned.LearningResources.Should().ContainSingle()
            .Which.Id.Should().Be(resourceA.Id);
    }

    private LearningResourceRepository ResourceRepository =>
        _provider.GetRequiredService<LearningResourceRepository>();

    private SkillProfileRepository SkillRepository =>
        _provider.GetRequiredService<SkillProfileRepository>();

    private static UserProfile CreateUser(string id) => new()
    {
        Id = id,
        Name = id,
        NativeLanguage = "English",
        TargetLanguage = "Korean",
        PreferredSessionMinutes = 20,
        CreatedAt = DateTime.UtcNow,
    };

    private LearningResource SeedResource(string userId, string? title = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = new LearningResource
        {
            Title = title ?? $"Resource {Guid.NewGuid()}",
            MediaType = "Vocabulary List",
            Language = "Korean",
            UserProfileId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.LearningResources.Add(resource);
        db.SaveChanges();
        return resource;
    }

    private SkillProfile SeedSkill(string userId, string? title = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var skill = new SkillProfile
        {
            Title = title ?? $"Skill {Guid.NewGuid()}",
            UserProfileId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.SkillProfiles.Add(skill);
        db.SaveChanges();
        return skill;
    }

    private VocabularyWord SeedWord(string? targetTerm = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = new VocabularyWord
        {
            TargetLanguageTerm = targetTerm ?? $"target-{Guid.NewGuid()}",
            NativeLanguageTerm = $"native-{Guid.NewGuid()}",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.VocabularyWords.Add(word);
        db.SaveChanges();
        return word;
    }

    private void Map(string resourceId, string vocabularyWordId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            ResourceId = resourceId,
            VocabularyWordId = vocabularyWordId,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        public int SelectCount { get; private set; }
        public int UpdateCount { get; private set; }
        public string? LastUpdateCommand { get; private set; }
        public bool ThrowOnUpdate { get; set; }

        public void Reset()
        {
            SelectCount = 0;
            UpdateCount = 0;
            LastUpdateCommand = null;
        }

        private void Inspect(DbCommand command)
        {
            var text = command.CommandText.TrimStart();
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                SelectCount++;
            if (text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                UpdateCount++;
                LastUpdateCommand = text;
                if (ThrowOnUpdate)
                    throw new InvalidOperationException("Simulated database update failure.");
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Inspect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }
    }
}
