using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Durable turn operations against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency and single-writer leasing are the two guarantees a coach turn cannot survive a
/// restart without, and both are enforced by the database rather than by application logic: the
/// unique <c>(UserProfileId, ConversationId, KeyDigest)</c> index is what makes "one claim wins"
/// true even when the two claimants never see each other's uncommitted rows.
/// </para>
/// <para>
/// An in-memory provider cannot prove any of that. Its writers share one connection, so the
/// losing insert is rejected by a tracked-entity check instead of by the index, and a unique
/// violation there does not poison the transaction the way PostgreSQL's <c>25P02</c> does. These
/// tests run every claim on its own connection so the race is genuinely a race.
/// </para>
/// </remarks>
public sealed class CoachPostgresTurnOperationTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;
    private string _conversationId = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("ops");

        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);
        var created = await conversations.CreateAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.CreateConversation());
        created.Status.Should().Be(CoachHistoryStatus.Success);
        _conversationId = created.Conversation!.Id;
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task The_same_key_and_payload_claimed_at_once_yields_exactly_one_writer()
    {
        const int Workers = 12;

        var results = await ClaimConcurrentlyAsync(
            Workers,
            i => CoachHistorySamples.Claim(_conversationId, key: "idem-shared", leaseOwner: $"worker-{i}"));

        var claimed = results.Where(r => r.Outcome == CoachTurnClaimOutcome.Claimed).ToArray();
        claimed.Should().HaveCount(1, "the unique key digest index admits exactly one claimant");

        // Everyone else must be told the turn is already accounted for. Any other outcome would
        // let a caller either run the turn twice or report a spurious failure to the learner.
        results.Except(claimed).Should().OnlyContain(r =>
            r.Outcome == CoachTurnClaimOutcome.InProgress
            || r.Outcome == CoachTurnClaimOutcome.ReplayCompleted
            || r.Outcome == CoachTurnClaimOutcome.ConversationBusy);

        // And only one row exists, so the losers did not leave half-written operations behind.
        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachTurnOperation\"")).Should().Be(1);
    }

    [PostgresFact]
    public async Task A_replay_after_completion_returns_the_stored_outcome_instead_of_running_again()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-replay"));
        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var complete = await operations.CompleteAsync(
            CoachHistorySamples.Owner,
            claim.Operation!.Id,
            "worker-a",
            claim.FencingVersion,
            outcomePayload: "{\"reply\":\"stored\"}",
            outcomeSchemaVersion: 3,
            firstResponseSequence: 1,
            lastResponseSequence: 2);
        complete.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);

        // The retry must not re-run the turn; it must be handed the winner's output verbatim.
        await using var retryDb = _harness.NewContext();
        var retry = await _harness.NewTurnOperationStore(retryDb).ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-replay"));

        retry.Outcome.Should().Be(CoachTurnClaimOutcome.ReplayCompleted);
        retry.StoredOutcome.Should().Be("{\"reply\":\"stored\"}");
        retry.StoredOutcomeSchemaVersion.Should().Be(3);

        // A client that lost its response and polls by id gets the same answer.
        var outcome = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, claim.Operation.Id);
        outcome.Should().NotBeNull();
        outcome!.IsReadable.Should().BeTrue();
        outcome.Payload.Should().Be("{\"reply\":\"stored\"}");
        outcome.FirstResponseSequence.Should().Be(1);
        outcome.LastResponseSequence.Should().Be(2);

        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachTurnOperation\"")).Should().Be(1);
    }

    [PostgresFact]
    public async Task The_same_key_with_a_different_payload_is_refused_rather_than_silently_replayed()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var first = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-shape", payload: "{\"text\":\"original\"}"));
        first.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        await operations.CompleteAsync(
            CoachHistorySamples.Owner, first.Operation!.Id, "worker-a", first.FencingVersion,
            "{\"reply\":\"done\"}", 1, 1, 1);

        // Same retry key, different request. Replaying the old outcome here would answer a
        // question the learner never asked, so the store must refuse outright.
        await using var conflictDb = _harness.NewContext();
        var conflict = await _harness.NewTurnOperationStore(conflictDb).ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-shape", payload: "{\"text\":\"different\"}"));

        conflict.Outcome.Should().Be(CoachTurnClaimOutcome.PayloadConflict);
        conflict.StoredOutcome.Should().BeNull("a conflicting caller must not be shown another request's answer");

        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachTurnOperation\"")).Should().Be(1);
    }

    [PostgresFact]
    public async Task Independent_conversations_do_not_block_one_another()
    {
        await using var setupDb = _harness.NewContext();
        var conversations = _harness.NewConversationStore(setupDb);
        var otherIds = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var created = await conversations.CreateAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.CreateConversation($"parallel-{i}"));
            otherIds.Add(created.Conversation!.Id);
        }

        // The single-writer slot is per conversation, not per learner. If it were global, a busy
        // learner with several open threads would serialize behind whichever one claimed first.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = otherIds.Select(async (id, i) =>
        {
            await using var db = new CoachDbContext(_harness.DbOptions);
            var operations = _harness.NewTurnOperationStore(db);
            await start.Task;
            return await operations.ClaimAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Claim(id, key: $"idem-parallel-{i}", leaseOwner: $"worker-{i}"));
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(claims);

        results.Should().OnlyContain(r => r.Outcome == CoachTurnClaimOutcome.Claimed);
        results.Select(r => r.Operation!.ConversationId).Should().BeEquivalentTo(otherIds);
    }

    [PostgresFact]
    public async Task A_second_operation_cannot_take_the_slot_while_a_live_lease_holds_it()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var held = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-holder", lease: TimeSpan.FromMinutes(10)));
        held.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        await using var otherDb = _harness.NewContext();
        var intruder = await _harness.NewTurnOperationStore(otherDb).ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-other", leaseOwner: "worker-b"));

        intruder.Outcome.Should().Be(CoachTurnClaimOutcome.ConversationBusy,
            "a different turn must wait its turn rather than interleave with the one in flight");
    }

    [PostgresFact]
    public async Task Renewing_a_lease_keeps_the_slot_and_a_superseded_worker_is_told_it_lost()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-renew", lease: TimeSpan.FromMinutes(5)));
        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var before = await operations.GetAsync(CoachHistorySamples.Owner, claim.Operation!.Id);
        var renew = await operations.RenewLeaseAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-a", claim.FencingVersion, TimeSpan.FromMinutes(30));

        renew.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);
        renew.Operation!.LeaseExpiresAt.Should().BeAfter(before!.LeaseExpiresAt!.Value,
            "renewing must actually move the expiry, or a long turn dies mid-flight");

        // A stale fencing token is the signature of a worker that was already replaced.
        var stale = await operations.RenewLeaseAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-a", claim.FencingVersion - 1, TimeSpan.FromMinutes(5));
        stale.Outcome.Should().Be(CoachTurnFinalizeOutcome.LeaseLost);

        var wrongOwner = await operations.RenewLeaseAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-impostor", claim.FencingVersion, TimeSpan.FromMinutes(5));
        wrongOwner.Outcome.Should().Be(CoachTurnFinalizeOutcome.LeaseLost);
    }

    [PostgresFact]
    public async Task An_expired_lease_is_recovered_and_the_dead_worker_can_no_longer_finalize()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var dead = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-crash", leaseOwner: "worker-dead", lease: TimeSpan.FromMinutes(5)));
        dead.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        // Simulate the worker dying by expiring its lease in the database, which is exactly what
        // the passage of time would do. Nothing about the row's state changes otherwise. The
        // expiry has to be expressed against the harness's frozen clock rather than the database's
        // `now()`: the store judges expiry with its own TimeProvider, so a wall-clock timestamp
        // would be compared against a frozen one and the row would never look expired.
        var expiredAt = _harness.Time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        await _harness.ExecuteAsync(
            $"UPDATE \"CoachTurnOperation\" SET \"LeaseExpiresAt\" = '{expiredAt:yyyy-MM-dd HH:mm:ss}+00' WHERE \"Id\" = '{dead.Operation!.Id}'");

        await using var recoveryDb = _harness.NewContext();
        var expired = await _harness.NewTurnOperationStore(recoveryDb).ListExpiredAsync(CoachHistorySamples.Owner);
        expired.Should().ContainSingle(o => o.Id == dead.Operation.Id,
            "crash recovery has to be able to find the abandoned row");

        // The replacement takes over under the same key and gets a higher fencing version.
        await using var takeoverDb = _harness.NewContext();
        var takeover = await _harness.NewTurnOperationStore(takeoverDb).ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-crash", leaseOwner: "worker-live"));

        takeover.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);
        takeover.FencingVersion.Should().BeGreaterThan(dead.FencingVersion);
        takeover.Operation!.AttemptCount.Should().BeGreaterThan(dead.Operation.AttemptCount);

        // This is the whole point of fencing: the zombie wakes up and tries to write its result.
        // It must fail closed rather than overwrite the live worker's answer. The zombie gets its
        // own context because a revived worker is a separate process that reads the row fresh;
        // reusing the context that made the original claim would serve the stale tracked entity
        // and test EF's change tracker instead of the store's fencing check.
        await using var zombieDb = _harness.NewContext();
        var zombie = await _harness.NewTurnOperationStore(zombieDb).CompleteAsync(
            CoachHistorySamples.Owner, dead.Operation.Id, "worker-dead", dead.FencingVersion,
            "{\"reply\":\"from the dead\"}", 1, 1, 1);
        zombie.Outcome.Should().Be(CoachTurnFinalizeOutcome.LeaseLost);

        var winner = await _harness.NewTurnOperationStore(takeoverDb).CompleteAsync(
            CoachHistorySamples.Owner, takeover.Operation.Id, "worker-live", takeover.FencingVersion,
            "{\"reply\":\"from the living\"}", 1, 1, 1);
        winner.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);

        var outcome = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, dead.Operation.Id);
        outcome!.Payload.Should().Be("{\"reply\":\"from the living\"}");
    }

    [PostgresFact]
    public async Task Cancellation_is_durable_and_a_terminal_operation_refuses_further_writes()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-cancel"));

        var cancel = await operations.RequestCancelAsync(CoachHistorySamples.Owner, claim.Operation!.Id);
        cancel.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);

        // Durable means readable from a connection that never saw the request.
        await using var freshDb = _harness.NewContext();
        var seen = await _harness.NewTurnOperationStore(freshDb).GetAsync(CoachHistorySamples.Owner, claim.Operation.Id);
        seen!.CancelRequested.Should().BeTrue();

        var fail = await operations.FailAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-a", claim.FencingVersion, "coach.cancelled");
        fail.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);
        fail.Operation!.Status.Should().Be(CoachTurnOperationStatus.Cancelled,
            "an operation that was asked to stop and then reported failure stopped because it was "
            + "asked to; recording that as a plain failure would make a clean cancellation "
            + "indistinguishable from a crash in the history");
        fail.Operation.ErrorCode.Should().Be("coach.cancelled");

        var again = await operations.CompleteAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-a", claim.FencingVersion, "{}", 1, null, null);
        again.Outcome.Should().Be(CoachTurnFinalizeOutcome.AlreadyTerminal,
            "a finished operation is history, not a mutable slot");
    }

    [PostgresFact]
    public async Task An_intruder_can_neither_read_nor_finalize_another_learners_operation()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-private"));

        (await operations.GetAsync(CoachHistorySamples.Intruder, claim.Operation!.Id)).Should().BeNull();
        (await operations.GetOutcomeAsync(CoachHistorySamples.Intruder, claim.Operation.Id)).Should().BeNull();
        (await operations.ListExpiredAsync(CoachHistorySamples.Intruder)).Should().BeEmpty();

        var steal = await operations.CompleteAsync(
            CoachHistorySamples.Intruder, claim.Operation.Id, "worker-a", claim.FencingVersion, "{}", 1, null, null);
        steal.Outcome.Should().Be(CoachTurnFinalizeOutcome.NotFound);

        var cancel = await operations.RequestCancelAsync(CoachHistorySamples.Intruder, claim.Operation.Id);
        cancel.Outcome.Should().Be(CoachTurnFinalizeOutcome.NotFound);

        // The owner's operation is untouched by any of it.
        var mine = await operations.GetAsync(CoachHistorySamples.Owner, claim.Operation.Id);
        mine!.Status.Should().Be(CoachTurnOperationStatus.Running);
        mine.CancelRequested.Should().BeFalse();
    }

    [PostgresFact]
    public async Task An_empty_owner_is_refused_before_any_row_is_touched()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Empty,
            CoachHistorySamples.Claim(_conversationId, key: "idem-anon"));
        claim.Outcome.Should().Be(CoachTurnClaimOutcome.NoOwner);

        (await _harness.ScalarAsync<long>("SELECT count(*) FROM \"CoachTurnOperation\"")).Should().Be(0,
            "an unauthenticated claim must not create durable state");
    }

    [PostgresFact]
    public async Task A_claim_against_a_missing_conversation_is_refused_by_the_owner_scoped_lookup()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var missing = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim("conv-does-not-exist", key: "idem-ghost"));
        missing.Outcome.Should().Be(CoachTurnClaimOutcome.ConversationNotFound);

        // The composite foreign key means this could never have been written anyway, but the
        // store must answer cleanly instead of surfacing a 23503 to the caller.
        var notMine = await operations.ClaimAsync(
            CoachHistorySamples.Intruder,
            CoachHistorySamples.Claim(_conversationId, key: "idem-ghost"));
        notMine.Outcome.Should().Be(CoachTurnClaimOutcome.ConversationNotFound,
            "another learner's conversation must be indistinguishable from one that does not exist");
    }

    /// <summary>Releases <paramref name="workers"/> claims at the same instant, each on its own connection.</summary>
    private async Task<CoachTurnClaimResult[]> ClaimConcurrentlyAsync(
        int workers,
        Func<int, ClaimCoachTurnRequest> request)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = Enumerable.Range(0, workers).Select(async i =>
        {
            await using var db = new CoachDbContext(_harness.DbOptions);
            var operations = _harness.NewTurnOperationStore(db);
            await start.Task;
            return await operations.ClaimAsync(CoachHistorySamples.Owner, request(i));
        }).ToArray();

        start.SetResult();
        return await Task.WhenAll(claims);
    }
}
