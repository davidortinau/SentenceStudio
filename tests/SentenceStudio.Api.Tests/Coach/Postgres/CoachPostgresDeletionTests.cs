using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Deletion;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Api.Tests.Coach.Memory;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Account deletion across checkpoint, history, and memory rows, against a real transaction.
/// </summary>
/// <remarks>
/// The deletion coordinator's central promise is that it is all-or-nothing: a learner who is told
/// their coach data was erased must not be left with half of it on disk, and a learner whose
/// deletion failed must keep an account that can retry. SQLite in shared-cache memory mode will
/// happily commit or roll back a transaction too, but it cannot demonstrate the interaction that
/// actually matters in production -- <c>ExecuteDeleteAsync</c> statements from several
/// contributors enrolled in one PostgreSQL transaction, where a failure part-way through has to
/// unwind server-side work that has already touched several tables.
/// </remarks>
public sealed class CoachPostgresDeletionTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("delete");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Deleting_an_account_clears_checkpoint_history_and_memory_together()
    {
        await SeedAsync(CoachHistorySamples.Owner);
        await SeedAsync(CoachHistorySamples.Intruder);

        await using var db = _harness.NewContext();
        var report = await NewService(db).DeleteAllForOwnerAsync(CoachHistorySamples.Owner);

        report.Succeeded.Should().BeTrue(report.FailureCode ?? "deletion should succeed");
        report.RowsDeleted.Should().BeGreaterThan(0);
        report.DeletesByContributor.Keys.Should().Contain(
            ["CoachCheckpoint", "CoachConversationHistory", "CoachMemoryFact"],
            "every lane that stores learner content must be represented, or the learner was told "
            + "something untrue about erasure");

        (await CountForAsync(CoachHistorySamples.Owner)).Should().Be(
            new Counts(0, 0, 0, 0, 0),
            "nothing the learner owned may survive the delete");

        var survivor = await CountForAsync(CoachHistorySamples.Intruder);
        survivor.Sessions.Should().Be(1);
        survivor.Conversations.Should().Be(1);
        survivor.Messages.Should().BeGreaterThan(0);
        survivor.Operations.Should().Be(1);
        survivor.Facts.Should().Be(1);
    }

    [PostgresFact]
    public async Task A_contributor_that_fails_leaves_every_other_table_untouched()
    {
        await SeedAsync(CoachHistorySamples.Owner);
        var before = await CountForAsync(CoachHistorySamples.Owner);

        await using var db = _harness.NewContext();

        // The real contributors run first and delete real rows; the last one then fails the way a
        // contributor would if its table were locked or its statement were rejected.
        var service = new CoachDataDeletionService(
            db,
            [
                new CoachCheckpointDeletionContributor(db, NullLogger<CoachCheckpointDeletionContributor>.Instance),
                _harness.NewHistoryDeletionContributor(db),
                new ThrowingContributor(),
            ],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(CoachHistorySamples.Owner);

        report.Succeeded.Should().BeFalse();
        report.FailureCode.Should().NotBeNullOrEmpty(
            "the caller needs a stable reason so account deletion can refuse rather than proceed");

        (await CountForAsync(CoachHistorySamples.Owner)).Should().Be(
            before,
            "a partial deletion is the worst outcome available: the learner would be told the "
            + "erasure failed while some of their conversations were already gone");
    }

    [PostgresFact]
    public async Task Deletion_is_idempotent_so_a_retry_after_a_crash_is_safe()
    {
        await SeedAsync(CoachHistorySamples.Owner);

        await using (var db = _harness.NewContext())
        {
            (await NewService(db).DeleteAllForOwnerAsync(CoachHistorySamples.Owner))
                .Succeeded.Should().BeTrue();
        }

        await using var second = _harness.NewContext();
        var again = await NewService(second).DeleteAllForOwnerAsync(CoachHistorySamples.Owner);

        again.Succeeded.Should().BeTrue(
            "a retry after a crash must not report failure just because the first attempt "
            + "already finished");
        again.RowsDeleted.Should().Be(0);
    }

    [PostgresFact]
    public async Task An_empty_owner_deletes_nothing_at_all()
    {
        await SeedAsync(CoachHistorySamples.Owner);
        var before = await CountForAsync(CoachHistorySamples.Owner);

        await using var db = _harness.NewContext();
        var report = await NewService(db).DeleteAllForOwnerAsync(CoachHistorySamples.Empty);

        report.Succeeded.Should().BeFalse();
        report.FailureCode.Should().Be("no_owner");
        (await CountForAsync(CoachHistorySamples.Owner)).Should().Be(
            before,
            "an absent owner is the one input that could delete the whole table if it were "
            + "treated as a wildcard");
    }

    private CoachDataDeletionService NewService(CoachDbContext db) =>
        new(
            db,
            [
                new CoachCheckpointDeletionContributor(db, NullLogger<CoachCheckpointDeletionContributor>.Instance),
                _harness.NewHistoryDeletionContributor(db),
                new CoachMemoryDeletionContributor(
                    _harness.NewMemoryStore(db, new RecordingNotifier()),
                    new RecordingNotifier(),
                    NullLogger<CoachMemoryDeletionContributor>.Instance),
            ],
            NullLogger<CoachDataDeletionService>.Instance);

    /// <summary>Puts one row of every learner-owned shape on disk for <paramref name="owner"/>.</summary>
    private async Task SeedAsync(CoachOwner owner)
    {
        await using var db = _harness.NewContext();

        var sessionStore = _harness.NewSessionStore(db);
        var session = await sessionStore.CreateAsync(owner.UserProfileId, CoachPersistenceSamples.CreateRequest());
        await sessionStore.AppendRevisionAsync(
            owner.UserProfileId,
            session.Id,
            CoachPersistenceSamples.RevisionInput());

        await _harness.NewUsageStore(db).RecordRunAsync(
            owner.UserProfileId,
            DateOnly.FromDateTime(_harness.Time.GetUtcNow().UtcDateTime),
            inputTokens: 120,
            outputTokens: 45,
            estimatedCostUsd: 0.01m);

        var conversation = await _harness.NewConversationStore(db)
            .CreateAsync(owner, CoachHistorySamples.CreateConversation());
        var conversationId = conversation.Conversation!.Id;

        await _harness.NewMessageStore(db)
            .AppendAsync(owner, CoachHistorySamples.Append(conversationId, CoachHistorySamples.LearnerText()));

        await _harness.NewTurnOperationStore(db)
            .ClaimAsync(owner, CoachHistorySamples.Claim(conversationId, $"key-{owner.UserProfileId}", "payload"));

        var memory = _harness.NewMemoryStore(db, new RecordingNotifier());
        var candidate = await memory.CreateCandidateAsync(
            owner,
            CoachMemorySamples.Candidate(conversationId: conversationId));
        candidate.Fact.Should().NotBeNull();
    }

    private async Task<Counts> CountForAsync(CoachOwner owner)
    {
        var id = owner.UserProfileId;

        return new Counts(
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "CoachSession" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "CoachConversation" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "CoachMessage" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "CoachTurnOperation" WHERE "UserProfileId" = '{id}'"""),
            await _harness.ScalarAsync<long>($"""SELECT count(*) FROM "CoachMemoryFact" WHERE "UserProfileId" = '{id}'"""));
    }

    private readonly record struct Counts(
        long Sessions,
        long Conversations,
        long Messages,
        long Operations,
        long Facts);

    /// <summary>Stands in for a contributor whose table is unavailable mid-deletion.</summary>
    private sealed class ThrowingContributor : ICoachDataDeletionContributor
    {
        public string Name => "Exploding";

        public Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("contributor failed mid-deletion");
    }
}
