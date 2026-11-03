using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class ActivitySessionServiceTests : IDisposable
{
    private const string UserA = "activity-user-a";
    private const string UserB = "activity-user-b";
    private const string ActivityType = "vocab-quiz";
    private const string ContextA = "context-a";
    private const string ContextB = "context-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public ActivitySessionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlite(_connection)
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        services.AddSingleton<ActivitySessionService>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task SaveSnapshotAsync_InsertsThenUpdatesAndAbandonsOlderDuplicateForSameContext()
    {
        var sut = GetService();

        var first = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"turn":1}""");

        first.Should().NotBeNull();
        first!.Status.Should().Be(ActivitySessionStatus.InProgress);
        first.StateJson.Should().Be("""{"turn":1}""");

        await DropUniqueInProgressIndexAsync();
        await InsertSessionAsync(new ActivitySession
        {
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = ContextA,
            StateJson = """{"turn":0}""",
            Status = ActivitySessionStatus.InProgress,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
        });

        var updated = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"turn":2}""");

        updated.Should().NotBeNull();
        updated!.Id.Should().Be(first.Id);
        updated.StateJson.Should().Be("""{"turn":2}""");
        updated.Status.Should().Be(ActivitySessionStatus.InProgress);

        var sessions = await GetSessionsAsync(UserA, ActivityType, ContextA);
        sessions.Should().HaveCount(2);
        sessions.Should().ContainSingle(s => s.Status == ActivitySessionStatus.InProgress)
            .Which.StateJson.Should().Be("""{"turn":2}""");
        sessions.Should().ContainSingle(s => s.Status == ActivitySessionStatus.Abandoned)
            .Which.StateJson.Should().Be("""{"turn":0}""");
    }

    [Fact]
    public async Task GetResumableAsync_ReturnsOnlyMatchingInProgressContextForSameUser()
    {
        var sut = GetService();
        var saved = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"state":"a"}""");
        await sut.SaveSnapshotAsync(UserA, ActivityType, ContextB, """{"state":"b"}""");
        await sut.SaveSnapshotAsync(UserB, ActivityType, ContextA, """{"state":"other-user"}""");

        var result = await sut.GetResumableAsync(UserA, ActivityType, ContextA);
        var differentContext = await sut.GetResumableAsync(UserA, ActivityType, "missing-context");
        var differentUser = await sut.GetResumableAsync(UserB, ActivityType, ContextB);

        result.Should().NotBeNull();
        result!.Id.Should().Be(saved!.Id);
        result.UserId.Should().Be(UserA);
        result.LaunchContextKey.Should().Be(ContextA);
        result.StateJson.Should().Be("""{"state":"a"}""");
        differentContext.Should().BeNull();
        differentUser.Should().BeNull("sessions saved under user A must not leak to user B");
    }

    [Fact]
    public async Task CompleteAsync_FlipsStatusSetsCompletedAtAndRemovesSessionFromResumableResults()
    {
        var sut = GetService();
        var saved = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"state":"complete-me"}""");

        await sut.CompleteAsync(UserA, ActivityType, ContextA);

        var persisted = await FindSessionAsync(saved.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ActivitySessionStatus.Completed);
        persisted.CompletedAt.Should().NotBeNull();
        persisted.UpdatedAt.Should().BeOnOrAfter(persisted.CompletedAt!.Value.AddSeconds(-1));

        var resumable = await sut.GetResumableAsync(UserA, ActivityType, ContextA);
        resumable.Should().BeNull();
    }

    [Fact]
    public async Task AbandonAsync_FlipsStatusAndRemovesSessionFromResumableResults()
    {
        var sut = GetService();
        var saved = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"state":"abandon-me"}""");

        await sut.AbandonAsync(UserA, saved!.Id);

        var persisted = await FindSessionAsync(saved.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ActivitySessionStatus.Abandoned);
        persisted.CompletedAt.Should().BeNull();

        var resumable = await sut.GetResumableAsync(UserA, ActivityType, ContextA);
        resumable.Should().BeNull();
    }

    [Fact]
    public async Task GetResumableAsync_ReturnsNullWhenStatusIsCompletedOrAbandoned()
    {
        await InsertSessionAsync(new ActivitySession
        {
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = "completed-context",
            StateJson = """{"done":true}""",
            Status = ActivitySessionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-4),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        });
        await InsertSessionAsync(new ActivitySession
        {
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = "abandoned-context",
            StateJson = """{"abandoned":true}""",
            Status = ActivitySessionStatus.Abandoned,
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
        });

        var sut = GetService();

        (await sut.GetResumableAsync(UserA, ActivityType, "completed-context")).Should().BeNull();
        (await sut.GetResumableAsync(UserA, ActivityType, "abandoned-context")).Should().BeNull();
    }

    [Fact]
    public async Task EmptyUserId_ReturnsNullAndSaveIsNoOp()
    {
        var sut = GetService();
        await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"owner":"a"}""");

        var emptyUserRead = await sut.GetResumableAsync(string.Empty, ActivityType, ContextA);
        var emptyUserSave = await sut.SaveSnapshotAsync(string.Empty, ActivityType, ContextA, """{"leak":"no"}""");

        emptyUserRead.Should().BeNull("empty user id must never fall through to an unfiltered query");
        emptyUserSave.Should().BeNull("empty user id writes must be safe no-ops");

        var allSessions = await GetAllSessionsAsync();
        allSessions.Should().ContainSingle();
        allSessions[0].UserId.Should().Be(UserA);
        allSessions[0].StateJson.Should().Be("""{"owner":"a"}""");
    }

    [Fact]
    public async Task CompleteAsync_CompletedContextIsNotOfferedAsResumable()
    {
        var sut = GetService();
        await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"state":"complete-context"}""");

        await sut.CompleteAsync(UserA, ActivityType, ContextA);

        (await sut.GetResumableAsync(UserA, ActivityType, ContextA)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_MarksAllInProgressRowsForScopedContextCompleted()
    {
        await DropUniqueInProgressIndexAsync();
        await InsertSessionAsync(new ActivitySession
        {
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = ContextA,
            StateJson = """{"turn":1}""",
            Status = ActivitySessionStatus.InProgress,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
        });
        await InsertSessionAsync(new ActivitySession
        {
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = ContextA,
            StateJson = """{"turn":2}""",
            Status = ActivitySessionStatus.InProgress,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var sut = GetService();
        await sut.CompleteAsync(UserA, ActivityType, ContextA);

        var sessions = await GetSessionsAsync(UserA, ActivityType, ContextA);
        sessions.Should().HaveCount(2);
        sessions.Should().OnlyContain(s => s.Status == ActivitySessionStatus.Completed);
        sessions.Should().OnlyContain(s => s.CompletedAt.HasValue);
        (await sut.GetResumableAsync(UserA, ActivityType, ContextA)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsyncAndAbandonAsync_EmptyUserIdAreNoOps()
    {
        var sut = GetService();
        var saved = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"state":"owner-a"}""");

        await sut.CompleteAsync(string.Empty, ActivityType, ContextA);
        await sut.AbandonAsync(string.Empty, saved!.Id);

        var persisted = await FindSessionAsync(saved.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ActivitySessionStatus.InProgress);
        persisted.CompletedAt.Should().BeNull();
        persisted.StateJson.Should().Be("""{"state":"owner-a"}""");
    }

    [Fact]
    public async Task AbandonAsync_OnlyAffectsMatchingUserId()
    {
        var sut = GetService();
        var userASession = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"owner":"a"}""");
        var userBSession = await sut.SaveSnapshotAsync(UserB, ActivityType, ContextA, """{"owner":"b"}""");

        await sut.AbandonAsync(UserA, userBSession!.Id);

        var persistedA = await FindSessionAsync(userASession!.Id);
        var persistedB = await FindSessionAsync(userBSession.Id);
        persistedA.Should().NotBeNull();
        persistedA!.Status.Should().Be(ActivitySessionStatus.InProgress);
        persistedB.Should().NotBeNull();
        persistedB!.Status.Should().Be(ActivitySessionStatus.InProgress);

        await sut.AbandonAsync(UserB, userBSession.Id);

        persistedB = await FindSessionAsync(userBSession.Id);
        persistedB.Should().NotBeNull();
        persistedB!.Status.Should().Be(ActivitySessionStatus.Abandoned);
        (await sut.GetResumableAsync(UserB, ActivityType, ContextA)).Should().BeNull();
        (await sut.GetResumableAsync(UserA, ActivityType, ContextA)).Should().NotBeNull();
    }

    [Fact]
    public async Task SaveSnapshotAsync_FilteredUniqueIndexRejectsDuplicateInProgressContext()
    {
        await InsertSessionAsync(new ActivitySession
        {
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = ContextA,
            StateJson = """{"turn":1}""",
            Status = ActivitySessionStatus.InProgress,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var duplicateInsert = () => InsertSessionAsync(new ActivitySession
        {
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = ContextA,
            StateJson = """{"turn":2}""",
            Status = ActivitySessionStatus.InProgress,
            StartedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await duplicateInsert.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// Phase 2b (docs/sync-all-tables-plan.md): one row per learner + activity + launch, so every
    /// client writes the same run. A later run of the launch reuses the row; the finished run's
    /// record is kept under its own ID.
    /// </summary>
    [Fact]
    public async Task SaveSnapshotAsync_UsesTheSharedRunId_AndALaterRunRevivesTheRow()
    {
        var sut = GetService();
        var first = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"run":1}""");
        first!.Id.Should().Be(SentenceStudio.Services.SyncIds.ActivitySession(UserA, ActivityType, ContextA));

        await sut.CompleteAsync(UserA, ActivityType, ContextA);
        var second = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"run":2}""");
        second!.Id.Should().Be(first.Id);
        second.Status.Should().Be(ActivitySessionStatus.InProgress);
        second.CompletedAt.Should().BeNull();
        second.StartedAt.Should().BeOnOrAfter(first.StartedAt);

        await sut.AbandonAsync(UserA, second.Id);
        var third = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"run":3}""");
        third!.Id.Should().Be(first.Id);

        // Each finished run keeps its own record; the shared row holds the current run.
        var rows = await GetSessionsAsync(UserA, ActivityType, ContextA);
        rows.Should().HaveCount(3);
        rows.Single(row => row.Id == first.Id).StateJson.Should().Be("""{"run":3}""");
        rows.Where(row => row.Id != first.Id).Select(row => (row.StateJson, row.Status))
            .Should().BeEquivalentTo(new[]
            {
                ("""{"run":1}""", ActivitySessionStatus.Completed),
                ("""{"run":2}""", ActivitySessionStatus.Abandoned)
            });
        rows.Single(row => row.StateJson == """{"run":1}""").CompletedAt.Should().NotBeNull();
        (await sut.GetResumableAsync(UserA, ActivityType, ContextA))!.Id.Should().Be(first.Id);
    }

    /// <summary>
    /// Phase 2b review: two clients that each start the launch again keep the finished run's
    /// record under the same ID, so after they sync there is one record, not one per client.
    /// </summary>
    [Fact]
    public async Task SaveSnapshotAsync_RecordAnotherClientAlreadyKept_IsNotDuplicated()
    {
        var sut = GetService();
        var first = await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"run":1}""");
        await sut.CompleteAsync(UserA, ActivityType, ContextA);
        var finished = (await GetSessionsAsync(UserA, ActivityType, ContextA)).Single();
        // Another client revived the same run first; its record arrived by sync.
        await InsertSessionAsync(new ActivitySession
        {
            Id = SentenceStudio.Services.SyncIds.ActivitySessionRecord(first!.Id, finished.StartedAt),
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = ContextA,
            StateJson = finished.StateJson,
            Status = finished.Status,
            StartedAt = finished.StartedAt,
            UpdatedAt = finished.UpdatedAt,
            CompletedAt = finished.CompletedAt
        });

        await sut.SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"run":2}""");

        var rows = await GetSessionsAsync(UserA, ActivityType, ContextA);
        rows.Should().HaveCount(2);
        rows.Single(row => row.Id == first.Id).StateJson.Should().Be("""{"run":2}""");
        rows.Single(row => row.Id != first.Id).StateJson.Should().Be("""{"run":1}""");
    }

    [Fact]
    public async Task SaveSnapshotAsync_KeepsUpdatingARunSavedBeforeRunIdsWereShared()
    {
        await InsertSessionAsync(new ActivitySession
        {
            Id = "legacy-run",
            UserId = UserA,
            ActivityType = ActivityType,
            LaunchContextKey = ContextA,
            StateJson = """{"turn":1}""",
            Status = ActivitySessionStatus.InProgress,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var saved = await GetService().SaveSnapshotAsync(UserA, ActivityType, ContextA, """{"turn":2}""");

        saved!.Id.Should().Be("legacy-run");
        (await GetSessionsAsync(UserA, ActivityType, ContextA)).Should().ContainSingle();
    }

    private ActivitySessionService GetService()
    {
        return _provider.GetRequiredService<ActivitySessionService>();
    }

    private async Task InsertSessionAsync(ActivitySession session)
    {
        // A row saved before run IDs were shared (or any legacy row) has its own random ID.
        if (string.IsNullOrEmpty(session.Id))
            session.Id = Guid.NewGuid().ToString();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ActivitySessions.Add(session);
        await db.SaveChangesAsync();
    }

    private async Task<ActivitySession?> FindSessionAsync(string id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ActivitySessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
    }

    private async Task<List<ActivitySession>> GetSessionsAsync(string userId, string activityType, string launchContextKey)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ActivitySessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.ActivityType == activityType && s.LaunchContextKey == launchContextKey)
            .OrderBy(s => s.Id)
            .ToListAsync();
    }

    private async Task<List<ActivitySession>> GetAllSessionsAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ActivitySessions.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
    }

    private async Task DropUniqueInProgressIndexAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_ActivitySession_UserId_LaunchContextKey\"");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_ActivitySession_UserId_ActivityType_LaunchContextKey\"");
    }
}
