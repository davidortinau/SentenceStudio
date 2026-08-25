using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Revision ordering, undo bookkeeping, and the no-raw-learner-text guarantee for the
/// coach audit trail.
/// </summary>
public class CoachPlanRevisionStoreTests
{
    [Fact]
    public async Task AppendRevisionAsync_AssignsSequentialNumbersPerSession()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var first = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        var second = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, first.Id, CoachPersistenceSamples.RevisionInput("v1", "v2"));
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, first.Id, CoachPersistenceSamples.RevisionInput("v2", "v3"));
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, second.Id, CoachPersistenceSamples.RevisionInput("v1", "v2"));

        var firstRevisions = await store.GetRevisionsAsync(CoachPersistenceSamples.OwnerUserId, first.Id);
        firstRevisions.Select(r => r.RevisionNumber).Should().Equal(1, 2);
        firstRevisions.Select(r => r.AfterPlanVersion).Should().Equal("v2", "v3");

        var secondRevisions = await store.GetRevisionsAsync(CoachPersistenceSamples.OwnerUserId, second.Id);
        secondRevisions.Should().ContainSingle().Which.RevisionNumber.Should().Be(1,
            "revision numbers restart per session, not per learner");
    }

    [Fact]
    public async Task GetRevisionsAsync_ReturnsAscendingOrder_RegardlessOfInsertOrder()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);
        var session = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        for (var i = 1; i <= 5; i++)
        {
            await store.AppendRevisionAsync(
                CoachPersistenceSamples.OwnerUserId,
                session.Id,
                CoachPersistenceSamples.RevisionInput($"v{i}", $"v{i + 1}"));
            harness.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var revisions = await store.GetRevisionsAsync(CoachPersistenceSamples.OwnerUserId, session.Id);
        revisions.Select(r => r.RevisionNumber).Should().BeInAscendingOrder();

        var latest = await store.GetLatestRevisionAsync(CoachPersistenceSamples.OwnerUserId, session.Id);
        latest!.RevisionNumber.Should().Be(5);
    }

    [Fact]
    public async Task AppendRevisionAsync_StoresHashesAndPreservationCounts()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);
        var session = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var revision = await store.AppendRevisionAsync(
            CoachPersistenceSamples.OwnerUserId, session.Id, CoachPersistenceSamples.RevisionInput());

        revision!.BeforePlanHash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        revision.AfterPlanHash.Should().NotBe(revision.BeforePlanHash);
        revision.BeforePlanHash.Should().Be(CoachNormalizedJson.Hash(revision.BeforePlanSnapshotJson));
        revision.PreservedCompletedCount.Should().Be(1);
        revision.PreservedInProgressCount.Should().Be(2);

        var sessionRow = await db.CoachSessions.AsNoTracking().SingleAsync();
        sessionRow.RevisionCount.Should().Be(1);
    }

    [Fact]
    public async Task MarkRevisionUndoneAsync_IsOwnedAndIdempotent()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);
        var session = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        var revision = await store.AppendRevisionAsync(
            CoachPersistenceSamples.OwnerUserId, session.Id, CoachPersistenceSamples.RevisionInput());

        (await store.MarkRevisionUndoneAsync(CoachPersistenceSamples.OtherUserId, revision!.Id, "undo-1")).Should().BeFalse();
        (await store.MarkRevisionUndoneAsync(CoachPersistenceSamples.OwnerUserId, revision.Id, "undo-1")).Should().BeTrue();
        (await store.MarkRevisionUndoneAsync(CoachPersistenceSamples.OwnerUserId, revision.Id, "undo-2")).Should().BeFalse();

        var row = await db.CoachPlanRevisions.AsNoTracking().SingleAsync();
        row.IsUndone.Should().BeTrue();
        row.UndoneByRevisionId.Should().Be("undo-1");
        row.UndoneAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Revision_StoresNoRawLearnerText()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        // The learner's words only ever reach the encrypted session blob. The revision
        // API surface has no parameter that can carry them.
        var session = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId,
            CoachPersistenceSamples.CreateRequest(agentSessionJson: $"{{\"learner\":\"{CoachPersistenceSamples.LearnerSentinel}\"}}"));
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, session.Id, CoachPersistenceSamples.RevisionInput());

        var row = await db.CoachPlanRevisions.AsNoTracking().SingleAsync();
        var allText = string.Join('|',
            row.AcceptedConstraintDeltaJson,
            row.BeforePlanSnapshotJson,
            row.AfterPlanSnapshotJson,
            row.BeforePlanVersion,
            row.AfterPlanVersion);

        allText.Should().NotContain(CoachPersistenceSamples.LearnerSentinel);
    }

    [Fact]
    public void RevisionEntity_ExposesNoFreeTextColumn()
    {
        // Structural guard: a future field named Message/Prompt/Transcript would let raw
        // learner text into the audit trail without anyone noticing at review time.
        var forbidden = new[] { "text", "message", "prompt", "transcript", "question", "utterance", "note", "content" };

        var offenders = typeof(CoachPlanRevision)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        offenders.Should().BeEmpty("the revision audit must never gain a free-text column");
    }

    [Fact]
    public async Task RevisionSourceAndIntent_AreRoundTripped()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);
        var session = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var input = CoachPersistenceSamples.RevisionInput();
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, session.Id, input);

        var row = await db.CoachPlanRevisions.AsNoTracking().SingleAsync();
        row.Source.Should().Be(CoachRevisionSource.DirectRequest);
        row.IntentKind.Should().Be(input.IntentKind);

        var delta = CoachNormalizedJson.Deserialize<CoachConstraintDeltaDto>(row.AcceptedConstraintDeltaJson);
        delta!.AvailableMinutes.Should().Be(12);
    }
}
