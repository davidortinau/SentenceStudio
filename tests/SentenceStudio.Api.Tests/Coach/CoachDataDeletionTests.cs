using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Deletion;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Account deletion must erase every coach row the learner owns, and must refuse to report
/// success if it cannot.
/// </summary>
/// <remarks>
/// The defect these cover: <c>DeleteAccount</c> removed the identity user and the user profile
/// but never touched the coach tables, leaving protected conversation state keyed to a
/// <c>UserProfileId</c> that no longer resolved to anyone — unreachable through the app, still
/// present in the database and in every later backup.
/// </remarks>
public class CoachDataDeletionTests
{
    [Fact]
    public async Task DeleteAllForOwner_RemovesSessionsRevisionsAndUsage()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var session = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await store.AppendRevisionAsync(
            CoachPersistenceSamples.OwnerUserId, session.Id, CoachPersistenceSamples.RevisionInput());
        await harness.NewUsageStore(db).RecordRunAsync(
            CoachPersistenceSamples.OwnerUserId, new DateOnly(2026, 8, 14), 10, 10, 0.01m);

        var report = await NewService(harness, db).DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();
        report.FailureCode.Should().BeNull();
        report.RowsDeleted.Should().Be(3);

        (await db.CoachSessions.CountAsync()).Should().Be(0);
        (await db.CoachPlanRevisions.CountAsync()).Should().Be(0);
        (await db.CoachUsages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAllForOwner_LeavesOtherLearnersUntouched()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        var survivor = await store.CreateAsync(
            CoachPersistenceSamples.OtherUserId, CoachPersistenceSamples.CreateRequest());

        var report = await NewService(harness, db).DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();

        var remaining = await db.CoachSessions.AsNoTracking().ToListAsync();
        remaining.Should().ContainSingle().Which.Id.Should().Be(survivor.Id,
            "a deletion scoped to one owner must never widen to another learner's rows");
    }

    [Fact]
    public async Task DeleteAllForOwner_IsIdempotent()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await harness.NewSessionStore(db).CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var service = NewService(harness, db);
        var owner = Owner(CoachPersistenceSamples.OwnerUserId);

        (await service.DeleteAllForOwnerAsync(owner)).Succeeded.Should().BeTrue();

        var second = await service.DeleteAllForOwnerAsync(owner);

        second.Succeeded.Should().BeTrue("a repeat erasure request has nothing to do and must not fail");
        second.RowsDeleted.Should().Be(0);
    }

    [Fact]
    public async Task DeleteAllForOwner_RunsEveryRegisteredContributor()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await harness.NewSessionStore(db).CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var spy = new RecordingContributor();
        var service = new CoachDataDeletionService(
            db,
            [NewCheckpointContributor(db), harness.NewDeletionContributor(db), spy],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();
        report.DeletesByContributor.Should().ContainKeys("CoachCheckpoint", "CoachConversationHistory", spy.Name);
        spy.Calls.Should().BeGreaterThan(0,
            "a contributor registered by another lane must be discovered, not hard-coded into a list here");
    }

    [Fact]
    public async Task DeleteAllForOwner_FailsWhenAContributorLeavesRowsBehind()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        // Reports a deletion it never performed — the exact shape of a silently broken filter.
        var lying = new LyingContributor();
        var service = new CoachDataDeletionService(
            db, [lying], NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse(
            "the verification pass exists so a contributor cannot report success while rows survive");
        report.FailureCode.Should().Be("verification_failed");
    }

    [Fact]
    public async Task DeleteAllForOwner_FailsClosedWhenAContributorThrows()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var service = new CoachDataDeletionService(
            db,
            [NewCheckpointContributor(db), new ThrowingContributor()],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();
        report.FailureCode.Should().Be("deletion_failed");
    }

    [Fact]
    public async Task DeleteAllForOwner_RefusesWhenNoContributorsAreRegistered()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var service = new CoachDataDeletionService(db, [], NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse(
            "an unregistered coordinator would otherwise report a successful erasure that deleted nothing");
        report.FailureCode.Should().Be("no_contributors");
    }

    [Fact]
    public async Task DeleteAllForOwner_RefusesAnEmptyOwner()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await harness.NewSessionStore(db).CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var report = await NewService(harness, db).DeleteAllForOwnerAsync(default);

        report.Succeeded.Should().BeFalse();
        report.FailureCode.Should().Be("no_owner");
        (await db.CoachSessions.CountAsync()).Should().Be(1,
            "an owner-less delete has no filter, so running it would erase every learner's rows");
    }

    [Fact]
    public async Task DeletionReport_CarriesNoLearnerIdentifier()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await harness.NewSessionStore(db).CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var report = await NewService(harness, db).DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.ToString().Should().NotContain(CoachPersistenceSamples.OwnerUserId,
            "the report is logged, and a learner identifier in a retention log outlives the deletion");
    }

    [Fact]
    public async Task DeleteAllForOwner_DeletesOwnedLegacyConversationRows()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var conversations = new FakeConversationOwnerDataService
        {
            OwnedConversations = 2,
            OwnedChunks = 7,
            UnownedConversations = 41
        };

        var service = new CoachDataDeletionService(
            db,
            [NewCheckpointContributor(db), harness.NewDeletionContributor(db), NewLegacyContributor(conversations)],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();
        report.DeletesByContributor.Should().ContainKey("LegacyConversation")
              .WhoseValue.Should().Be(9);

        // Called twice, always with the leaving learner's id: once to delete, once for the
        // coordinator's verification pass, which only passes because the second call finds
        // nothing left. A contributor that quietly skipped rows would fail here.
        conversations.DeletedForUserProfileIds.Should()
                     .OnlyContain(id => id == CoachPersistenceSamples.OwnerUserId)
                     .And.HaveCount(2);
    }

    [Fact]
    public async Task DeleteAllForOwner_LeavesOwnerlessLegacyRowsAlone()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var conversations = new FakeConversationOwnerDataService
        {
            OwnedConversations = 1,
            OwnedChunks = 1,
            UnownedConversations = 41,
            UnownedChunks = 128
        };

        var service = new CoachDataDeletionService(
            db, [NewLegacyContributor(conversations)], NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();
        conversations.UnownedConversations.Should().Be(41,
            "rows with no owner predate scoping, and attributing them to whoever is leaving is a guess");
        conversations.UnownedChunks.Should().Be(128);
    }

    [Fact]
    public async Task DeletionReport_NeverMentionsUnattributedLegacyRows()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var conversations = new FakeConversationOwnerDataService
        {
            OwnedConversations = 1,
            UnownedConversations = 41
        };

        var service = new CoachDataDeletionService(
            db, [NewLegacyContributor(conversations)], NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.RowsDeleted.Should().Be(1,
            "the count the learner is shown must describe their own data and nothing else");
        conversations.UnownedDiagnosticsRequested.Should().BeFalse(
            "unowned counts are operator diagnostics; pulling them into a user-facing deletion path " +
            "is how they end up in a user-facing message");
    }

    [Fact]
    public async Task DeleteAllForOwner_CoversCoachHistoryAndLegacyRowsTogether()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var session = await store.CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await store.AppendRevisionAsync(
            CoachPersistenceSamples.OwnerUserId, session.Id, CoachPersistenceSamples.RevisionInput());

        var conversations = new FakeConversationOwnerDataService { OwnedConversations = 3 };

        var service = new CoachDataDeletionService(
            db,
            [NewCheckpointContributor(db), harness.NewDeletionContributor(db), NewLegacyContributor(conversations)],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();
        report.DeletesByContributor.Should().ContainKeys(
            "CoachCheckpoint", "CoachConversationHistory", "LegacyConversation");

        (await db.CoachSessions.CountAsync()).Should().Be(0);
        (await db.CoachPlanRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task LegacyContributor_RefusesAnEmptyOwner()
    {
        var conversations = new FakeConversationOwnerDataService { OwnedConversations = 5 };

        var deleted = await NewLegacyContributor(conversations).DeleteAllAsync(default);

        deleted.Should().Be(0);
        conversations.DeletedForUserProfileIds.Should().BeEmpty(
            "an owner-less call would otherwise reach a delete path with no scoping");
    }

    [Fact]
    public async Task DeleteAllForOwner_FailsWhenLegacyDeletionSilentlyLeavesRowsBehind()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        // The shared conversation service reports a database failure as a zero-row result rather
        // than an exception, so a delete that never happened looks exactly like a learner who had
        // nothing to delete. Reporting erasure success over surviving rows is the one outcome
        // this whole coordinator exists to prevent.
        var conversations = new FakeConversationOwnerDataService
        {
            OwnedConversations = 3,
            SwallowDeletionFailure = true
        };

        var service = new CoachDataDeletionService(
            db, [NewLegacyContributor(conversations)], NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// A host whose contexts do not share a database cannot have one transaction, so the legacy
    /// delete must not run until the coach half is safely committed.
    /// </summary>
    /// <remarks>
    /// This is the shape the defect took. The legacy contributor writes through its own context and
    /// commits the instant it saves, so running it in the middle of a pass that can still fail meant
    /// a coach failure destroyed the learner's conversations and rolled back nothing that mattered —
    /// while the endpoint reported that nothing had been removed.
    /// </remarks>
    [Fact]
    public async Task WithoutASharedTransaction_TheLegacyDeleteDoesNotRunBeforeAFailure()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var conversations = new FakeConversationOwnerDataService { OwnedConversations = 3, OwnedChunks = 5 };

        var service = new CoachDataDeletionService(
            db,
            [NewLegacyContributor(conversations), NewCheckpointContributor(db), new ThrowingContributor()],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();
        report.DataWasRemoved.Should().BeFalse(
            "the coach work rolled back and the legacy work never started, so the caller may say "
            + "nothing was removed");

        conversations.DeletedForUserProfileIds.Should().BeEmpty(
            "a delete that commits on its own connection can never be undone by this rollback, so it "
            + "must not happen until the rest of the erasure has committed");
    }

    /// <summary>
    /// When the deferred half fails, the coach half is already gone and the report has to say so.
    /// </summary>
    [Fact]
    public async Task WhenTheDeferredDeleteFails_TheReportSaysDataWasAlreadyRemoved()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await harness.NewSessionStore(db).CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        // Reproduces the real service swallowing a database error as a zero result: the rows
        // survive, so the contributor's own guard fails the erasure.
        var conversations = new FakeConversationOwnerDataService
        {
            OwnedConversations = 3,
            SwallowDeletionFailure = true
        };

        var service = new CoachDataDeletionService(
            db,
            [NewLegacyContributor(conversations), NewCheckpointContributor(db)],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();
        report.DataWasRemoved.Should().BeTrue(
            "the coach rows were committed before the legacy half failed, so telling the learner "
            + "nothing was removed would be false");

        (await db.CoachSessions.CountAsync()).Should().Be(0, "the committed half stays committed");
    }

    /// <summary>
    /// The case counts alone cannot see: a deferred delete that commits and then fails before it
    /// can report what it removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deferred contributors run after the coach commit, on their own connections. A
    /// contributor that commits its delete and then throws — its own post-delete verification read
    /// failing, which is exactly what the real legacy contributor does — destroys rows and returns
    /// no count. With no coach rows to delete, every count the coordinator holds is then zero.
    /// </para>
    /// <para>
    /// A report assembled from counts alone would therefore say nothing was removed, and the
    /// endpoint would tell the learner their data is intact while it is already gone. That is the
    /// same lie this whole coordinator exists to prevent, arriving through a different door.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenADeferredDeleteCommitsAndThenThrows_TheReportDoesNotClaimNothingWasRemoved()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        // No coach rows on purpose. That is what pins every recorded count to zero and leaves the
        // committed external delete as the only thing that happened.
        var external = new CommittedThenFailingExternalContributor();

        var service = new CoachDataDeletionService(
            db,
            [NewCheckpointContributor(db), external],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();

        external.RowsCommitted.Should().BeGreaterThan(
            0, "the scenario only means anything if rows were genuinely destroyed");
        report.DeletesByContributor.Should().NotContainKey(
            external.Name,
            "the contributor threw before it could report a count — which is precisely why a count "
            + "cannot be the only input to a claim about the learner's data");

        report.DataWasRemoved.Should().BeTrue(
            "the delete had already committed when it threw, so 'nothing was removed' is a "
            + "sentence this report must never support");
    }

    /// <summary>
    /// The same hole, reached by cancellation rather than by a failure.
    /// </summary>
    /// <remarks>
    /// A learner who closes the app mid-request cancels the token, and the cancellation lands
    /// wherever it lands — including between an external store's commit and its return. The rows
    /// are just as gone as they are after an exception.
    /// </remarks>
    [Fact]
    public async Task WhenADeferredDeleteIsCancelledAfterCommitting_TheReportDoesNotClaimNothingWasRemoved()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var external = new CommittedThenFailingExternalContributor(
            () => new OperationCanceledException("the request was cancelled after the delete committed"));

        var service = new CoachDataDeletionService(
            db,
            [NewCheckpointContributor(db), external],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();
        external.RowsCommitted.Should().BeGreaterThan(0);
        report.DataWasRemoved.Should().BeTrue(
            "a cancellation after the commit destroys exactly as much data as an exception does");
    }

    /// <summary>
    /// The guard has to stay narrow: a failure that happens before any unrecoverable delete is
    /// still an honest "nothing was removed".
    /// </summary>
    [Fact]
    public async Task WhenTheFailureHappensBeforeTheDeferredPass_TheReportStillSaysNothingWasRemoved()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var external = new CommittedThenFailingExternalContributor();

        var service = new CoachDataDeletionService(
            db,
            [NewCheckpointContributor(db), new ThrowingContributor(), external],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();
        external.Invocations.Should().Be(
            0, "the deferred pass never starts once the transactional pass has failed");
        report.DataWasRemoved.Should().BeFalse(
            "the rollback restored everything and nothing outside it had begun, so the reassurance "
            + "is accurate and withholding it would frighten a learner for no reason");
    }

    [Fact]
    public async Task ASuccessfulDeletionReportsThatDataWasRemoved()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await harness.NewSessionStore(db).CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        var report = await NewService(harness, db).DeleteAllForOwnerAsync(
            Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();
        report.DataWasRemoved.Should().BeTrue();
    }

    [Fact]
    public async Task AnErasureWithNothingToRemoveDoesNotClaimDataWasRemoved()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var report = await NewService(harness, db).DeleteAllForOwnerAsync(
            Owner(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeTrue();
        report.RowsDeleted.Should().Be(0);
        report.DataWasRemoved.Should().BeFalse();
    }

    private static CoachOwner Owner(string userProfileId) => CoachOwner.ForUser(userProfileId);
    private static LegacyConversationDeletionContributor NewLegacyContributor(
        IConversationOwnerDataService conversations) =>
        new(conversations, NullLogger<LegacyConversationDeletionContributor>.Instance);

    private static CoachCheckpointDeletionContributor NewCheckpointContributor(CoachDbContext db) =>
        new(db, NullLogger<CoachCheckpointDeletionContributor>.Instance);

    private static CoachDataDeletionService NewService(CoachPersistenceHarness harness, CoachDbContext db) =>
        new(
            db,
            [NewCheckpointContributor(db), harness.NewDeletionContributor(db)],
            NullLogger<CoachDataDeletionService>.Instance);

    private sealed class RecordingContributor : ICoachDataDeletionContributor
    {
        public int Calls { get; private set; }

        public string Name => "TestLane";

        public Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(0);
        }
    }

    private sealed class LyingContributor : ICoachDataDeletionContributor
    {
        public string Name => "Lying";

        // Always claims one row, so the verification pass always sees a non-zero second result.
        public Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class ThrowingContributor : ICoachDataDeletionContributor
    {
        public string Name => "Throwing";

        public Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("contributor failure");
    }

    /// <summary>
    /// Stands in for the legacy conversation lane. Owned counts drain on the first delete so the
    /// coordinator's verification pass sees a genuinely idempotent contributor; unowned counts
    /// never move, which is the property under test.
    /// </summary>
    private sealed class FakeConversationOwnerDataService : IConversationOwnerDataService
    {
        public int OwnedConversations { get; set; }

        public int OwnedChunks { get; set; }

        public int UnownedConversations { get; set; }

        public int UnownedChunks { get; set; }

        public List<string> DeletedForUserProfileIds { get; } = [];

        public bool UnownedDiagnosticsRequested { get; private set; }

        /// <summary>Reproduces the real service swallowing a database error as a zero result.</summary>
        public bool SwallowDeletionFailure { get; set; }

        public Task<ConversationOwnedExport> ExportOwnedAsync(
            string userProfileId, CancellationToken cancellationToken = default)
        {
            if (OwnedConversations == 0)
            {
                return Task.FromResult(ConversationOwnedExport.Empty);
            }

            var surviving = Enumerable
                .Range(0, OwnedConversations)
                .Select(_ => new SentenceStudio.Shared.Models.Conversation { UserProfileId = userProfileId })
                .ToList();

            return Task.FromResult(new ConversationOwnedExport(surviving));
        }

        public Task<ConversationOwnedDeletionResult> DeleteOwnedAsync(
            string userProfileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userProfileId))
            {
                return Task.FromResult(ConversationOwnedDeletionResult.None);
            }

            DeletedForUserProfileIds.Add(userProfileId);

            if (SwallowDeletionFailure)
            {
                return Task.FromResult(ConversationOwnedDeletionResult.None);
            }

            var result = new ConversationOwnedDeletionResult(OwnedConversations, OwnedChunks);
            OwnedConversations = 0;
            OwnedChunks = 0;

            return Task.FromResult(result);
        }

        public Task<ConversationUnownedDiagnostics> GetUnownedDiagnosticsAsync(
            CancellationToken cancellationToken = default)
        {
            UnownedDiagnosticsRequested = true;
            return Task.FromResult(new ConversationUnownedDiagnostics(UnownedConversations, UnownedChunks));
        }
    }
}
