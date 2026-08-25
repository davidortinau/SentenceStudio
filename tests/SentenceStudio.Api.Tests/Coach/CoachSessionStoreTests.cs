using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Ownership, expiry, versioning, and deletion behaviour at the coach store boundary.
/// These are the guarantees the HTTP layer depends on to answer 404 without leaking
/// whether another learner's session exists.
/// </summary>
public class CoachSessionStoreTests
{
    [Fact]
    public async Task LoadAsync_OwnedSession_ReturnsFound()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        var result = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        result.Status.Should().Be(CoachSessionLoadStatus.Found);
        result.Session!.Id.Should().Be(created.Id);
        result.IsUsable.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_OtherUsersSession_IsIndistinguishableFromMissing()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var crossUser = await store.LoadAsync(CoachPersistenceSamples.OtherUserId, created.Id);
        var missing = await store.LoadAsync(CoachPersistenceSamples.OtherUserId, "does-not-exist");

        crossUser.Status.Should().Be(CoachSessionLoadStatus.NotFound,
            "a non-owner must not be able to tell an owned session apart from a missing one");
        crossUser.Session.Should().BeNull();
        missing.Status.Should().Be(crossUser.Status);
    }

    [Fact]
    public async Task Mutations_AreRefusedForNonOwner()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var intruder = CoachPersistenceSamples.OtherUserId;
        (await store.UpdateAsync(intruder, created.Id, new CoachSessionUpdate { TurnIncrement = 1 })).Should().BeFalse();
        (await store.SetPendingSuggestionAsync(intruder, created.Id, "sug-1", CoachPersistenceSamples.Delta())).Should().BeFalse();
        (await store.ClearPendingSuggestionAsync(intruder, created.Id)).Should().BeFalse();
        (await store.AppendRevisionAsync(intruder, created.Id, CoachPersistenceSamples.RevisionInput())).Should().BeNull();
        (await store.GetRevisionsAsync(intruder, created.Id)).Should().BeEmpty();
        (await store.DeleteAsync(intruder, created.Id)).Should().BeFalse();

        var stillThere = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);
        stillThere.Status.Should().Be(CoachSessionLoadStatus.Found);
        stillThere.Session!.TurnCount.Should().Be(0);
        stillThere.Session.PendingSuggestionId.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyUserId_ReturnsNoData_AndNeverThrows(string userId)
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        (await store.LoadAsync(userId, created.Id)).Status.Should().Be(CoachSessionLoadStatus.NotFound);
        (await store.LoadResumableAsync(userId)).Status.Should().Be(CoachSessionLoadStatus.NotFound);
        (await store.UpdateAsync(userId, created.Id, new CoachSessionUpdate())).Should().BeFalse();
        (await store.GetRevisionsAsync(userId, created.Id)).Should().BeEmpty();
        (await store.DeleteAsync(userId, created.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WithoutUserId_Throws_RatherThanWritingAnOrphanRow()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var act = () => store.CreateAsync("  ", CoachPersistenceSamples.CreateRequest());

        await act.Should().ThrowAsync<ArgumentException>();
        (await db.CoachSessions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_PastExpiry_IsRejectedAndMarkedExpired()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        created.ExpiresAt.Should().Be(harness.Time.GetUtcNow().UtcDateTime + TimeSpan.FromHours(24));

        harness.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        var result = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        result.Status.Should().Be(CoachSessionLoadStatus.Expired);
        result.Session.Should().BeNull();

        var row = await db.CoachSessions.AsNoTracking().SingleAsync(s => s.Id == created.Id);
        row.Status.Should().Be(CoachSessionStatus.Expired);
        row.StopReason.Should().Be(CoachStopReason.SessionExpired);
    }

    [Fact]
    public async Task LoadAsync_SlidesExpiryForward()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        harness.Time.Advance(TimeSpan.FromHours(23));
        var result = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);
        result.Status.Should().Be(CoachSessionLoadStatus.Found);

        harness.Time.Advance(TimeSpan.FromHours(23));
        var stillAlive = await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);
        stillAlive.Status.Should().Be(CoachSessionLoadStatus.Found,
            "a read inside the window pushes the 24h sliding expiry forward");
    }

    [Fact]
    public async Task LoadAsync_ConfigVersionMismatch_IsRejected()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var created = await harness.NewSessionStore(db)
            .CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        created.AgentConfigVersion.Should().Be(harness.Options.AgentConfigVersion);

        harness.Options.AgentConfigVersion = "2026-08-14.2";
        var afterConfigBump = await harness.NewSessionStore(db).LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        afterConfigBump.Status.Should().Be(CoachSessionLoadStatus.ConfigVersionMismatch);
        afterConfigBump.Session.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_SchemaVersionMismatch_IsRejected()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var created = await harness.NewSessionStore(db)
            .CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        harness.Options.SessionSchemaVersion += 1;
        var result = await harness.NewSessionStore(db).LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        result.Status.Should().Be(CoachSessionLoadStatus.ConfigVersionMismatch);
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent_AndRemovesPendingState()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId,
            CoachPersistenceSamples.CreateRequest(agentSessionJson: $"{{\"turn\":\"{CoachPersistenceSamples.LearnerSentinel}\"}}"));
        await store.SetPendingSuggestionAsync(CoachPersistenceSamples.OwnerUserId, created.Id, "sug-1", CoachPersistenceSamples.Delta());

        (await store.DeleteAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Should().BeTrue();
        (await store.DeleteAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Should().BeFalse("a repeat delete is a no-op, not an error");

        (await db.CoachSessions.CountAsync()).Should().Be(0, "the encrypted conversation state and pending suggestion are hard-deleted");
        (await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Status.Should().Be(CoachSessionLoadStatus.NotFound);
    }

    [Fact]
    public async Task DeleteAsync_KeepsTheRevisionAudit()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, created.Id, CoachPersistenceSamples.RevisionInput());

        await store.DeleteAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        (await db.CoachPlanRevisions.CountAsync()).Should().Be(1,
            "deleting coach history must not erase the audit of a plan change it already applied");
    }

    [Fact]
    public async Task DeleteAsync_WorksForAnExpiredSession()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        harness.Time.Advance(TimeSpan.FromDays(3));

        (await store.DeleteAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Should().BeTrue(
            "a learner can always erase conversation state, even for a session the server would refuse to resume");
    }

    [Fact]
    public async Task PendingSuggestion_RoundTripsAndClears()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        (await store.SetPendingSuggestionAsync(CoachPersistenceSamples.OwnerUserId, created.Id, "sug-1", CoachPersistenceSamples.Delta(9)))
            .Should().BeTrue();

        var pending = await store.GetPendingSuggestionAsync(CoachPersistenceSamples.OwnerUserId, created.Id, "sug-1");
        pending!.AvailableMinutes.Should().Be(9);
        pending.ChangedFields.Should().ContainSingle().Which.Should().Be(CoachConstraintField.AvailableMinutes);

        (await store.GetPendingSuggestionAsync(CoachPersistenceSamples.OwnerUserId, created.Id, "sug-other"))
            .Should().BeNull("a suggestion id that does not match the pending one resolves to nothing");
        (await store.GetPendingSuggestionAsync(CoachPersistenceSamples.OtherUserId, created.Id, "sug-1"))
            .Should().BeNull();

        (await store.ClearPendingSuggestionAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Should().BeTrue();
        (await store.ClearPendingSuggestionAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Should().BeFalse();

        var row = await db.CoachSessions.AsNoTracking().SingleAsync();
        row.PendingSuggestionId.Should().BeNull();
        row.PendingSuggestionDeltaJson.Should().BeNull();
        row.Status.Should().Be(CoachSessionStatus.Active);
    }

    [Fact]
    public async Task LoadResumableAsync_IgnoresExpiredAndOtherUsers()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        await store.CreateAsync(CoachPersistenceSamples.OtherUserId, CoachPersistenceSamples.CreateRequest());
        var mine = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        (await store.LoadResumableAsync(CoachPersistenceSamples.OwnerUserId)).Session!.Id.Should().Be(mine.Id);

        harness.Time.Advance(TimeSpan.FromDays(2));
        (await store.LoadResumableAsync(CoachPersistenceSamples.OwnerUserId)).Status.Should().Be(CoachSessionLoadStatus.NotFound);
    }

    [Fact]
    public async Task UpdateAsync_AppliesPartialChangesAndIncrementsCounts()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        await store.UpdateAsync(CoachPersistenceSamples.OwnerUserId, created.Id, new CoachSessionUpdate
        {
            TurnIncrement = 1,
            ClarificationIncrement = 1,
            Status = CoachSessionStatus.AwaitingClarification,
            ActiveConstraints = CoachPersistenceSamples.Constraints(45)
        });
        await store.UpdateAsync(CoachPersistenceSamples.OwnerUserId, created.Id, new CoachSessionUpdate { TurnIncrement = 1 });

        var row = await db.CoachSessions.AsNoTracking().SingleAsync();
        row.TurnCount.Should().Be(2);
        row.ClarificationCount.Should().Be(1);
        row.Status.Should().Be(CoachSessionStatus.AwaitingClarification);
        row.ActiveConstraintsJson.Should().Contain("45");
    }

    [Fact]
    public async Task ExpiredSession_IsNotWritable()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        harness.Time.Advance(TimeSpan.FromHours(25));

        (await store.UpdateAsync(CoachPersistenceSamples.OwnerUserId, created.Id, new CoachSessionUpdate { TurnIncrement = 1 })).Should().BeFalse();
        (await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, created.Id, CoachPersistenceSamples.RevisionInput())).Should().BeNull();
    }
}
