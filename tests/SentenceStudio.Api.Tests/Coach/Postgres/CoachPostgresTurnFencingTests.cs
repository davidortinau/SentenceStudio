using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// Fencing and lease renewal against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// The property under test is that a worker which has been superseded cannot put a second answer
/// in front of the learner. That is not a property of the application code — it is a property of
/// how the fencing check and the message insert are ordered against a concurrent takeover, and
/// only a real server with real row locks and real transaction isolation can decide it.
/// </para>
/// <para>
/// An in-memory or single-connection provider would pass a time-of-check/time-of-use
/// implementation just as happily as a correct one: with one connection there is no window to
/// slip through. <see cref="An_append_cannot_slip_past_an_uncommitted_takeover"/> is the test
/// that tells them apart, and it needs PostgreSQL's read-committed snapshot to do it.
/// </para>
/// </remarks>
public sealed class CoachPostgresTurnFencingTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private CoachPostgresHarness _harness = null!;
    private string _conversationId = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("fence");

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
    public async Task A_superseded_worker_cannot_append_a_learner_visible_message()
    {
        await using var stale = _harness.NewContext();
        await using var winner = _harness.NewContext();

        var staleOperations = _harness.NewTurnOperationStore(stale);
        var staleMessages = _harness.NewMessageStore(stale);
        var winnerOperations = _harness.NewTurnOperationStore(winner);
        var winnerMessages = _harness.NewMessageStore(winner);

        var first = await staleOperations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-fence", leaseOwner: "worker-first", lease: Lease));
        first.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var staleFence = new CoachTurnFence(first.Operation!.Id, "worker-first", first.FencingVersion);

        // Nobody renews, so the lease lapses and a replacement takes the conversation over.
        _harness.Time.Advance(Lease + TimeSpan.FromSeconds(1));

        var second = await winnerOperations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-fence", leaseOwner: "worker-second", lease: Lease));
        second.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);
        second.FencingVersion.Should().BeGreaterThan(staleFence.FencingVersion);

        // The first worker's model call finally returns and it tries to publish its answer.
        var refused = await staleMessages.AppendAsync(
            CoachHistorySamples.Owner,
            Append(CoachHistorySamples.CoachText("Answer from the replaced worker."), staleFence));

        refused.Status.Should().Be(CoachHistoryStatus.LeaseLost);
        refused.Message.Should().BeNull();

        var accepted = await winnerMessages.AppendAsync(
            CoachHistorySamples.Owner,
            Append(
                CoachHistorySamples.CoachText("Answer from the worker that holds the lease."),
                new CoachTurnFence(second.Operation!.Id, "worker-second", second.FencingVersion)));

        accepted.Status.Should().Be(CoachHistoryStatus.Success);

        (await CoachMessageCountAsync()).Should().Be(1, "one turn produced exactly one visible answer");
    }

    [PostgresFact]
    public async Task An_append_cannot_slip_past_an_uncommitted_takeover()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var first = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-toctou", leaseOwner: "worker-first", lease: Lease));
        first.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var staleFence = new CoachTurnFence(first.Operation!.Id, "worker-first", first.FencingVersion);

        // A takeover, held open. This is the exact window a check-then-write implementation loses
        // in: under read committed the uncommitted row is invisible to another session, so a plain
        // SELECT of the operation still reports the superseded fencing version as current, and an
        // insert made on the strength of that read lands anyway.
        await using var takeover = await _harness.OpenRawAsync();
        await using var transaction = await takeover.BeginTransactionAsync();
        await using (var command = takeover.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE "CoachTurnOperation"
                SET "FencingVersion" = "FencingVersion" + 1,
                    "AttemptCount" = "AttemptCount" + 1,
                    "LeaseOwner" = 'worker-second',
                    "Version" = "Version" + 1
                WHERE "Id" = @id
                """;
            command.Parameters.AddWithValue("id", first.Operation.Id);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        // The stale worker's append must not be admitted. Because the fence is taken by the same
        // statement that authorizes the write, it contends for the row the takeover already holds
        // rather than reading around it — so it waits, and the lock timeout ends it.
        //
        // The connection is opened explicitly so the session setting and the append share it. A
        // SET on a pooled connection that is handed back before the next call would configure a
        // session nothing runs on.
        await using var appending = _harness.NewContext();
        await appending.Database.OpenConnectionAsync();
        await appending.Database.ExecuteSqlRawAsync("SET lock_timeout = '2s'");
        var messages = _harness.NewMessageStore(appending);

        var refused = await AppendOrFaultAsync(
            messages,
            Append(CoachHistorySamples.CoachText("Answer that must never be seen."), staleFence));

        refused.Should().NotBe(
            CoachHistoryStatus.Success,
            "an append that can be admitted while a takeover is in flight is the duplicate this fence exists to stop");

        await appending.Database.CloseConnectionAsync();
        await transaction.CommitAsync();

        (await CoachMessageCountAsync()).Should().Be(
            0,
            "nothing the superseded worker produced reached the transcript");
    }

    [PostgresFact]
    public async Task A_renewed_lease_refuses_a_takeover_and_the_holder_still_appends()
    {
        await using var holder = _harness.NewContext();
        await using var challenger = _harness.NewContext();

        var holderOperations = _harness.NewTurnOperationStore(holder);
        var holderMessages = _harness.NewMessageStore(holder);
        var challengerOperations = _harness.NewTurnOperationStore(challenger);

        var claim = await holderOperations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-renewed", leaseOwner: "worker-live", lease: Lease));
        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var fence = new CoachTurnFence(claim.Operation!.Id, "worker-live", claim.FencingVersion);

        // The turn is slow. Time passes to just short of the grant, the heartbeat renews on its
        // own connection, and time passes again — past the point the original grant would have
        // lapsed.
        _harness.Time.Advance(TimeSpan.FromSeconds(80));

        await using var renewing = _harness.NewContext();
        var renewal = await _harness.NewTurnOperationStore(renewing).RenewLeaseAsync(
            CoachHistorySamples.Owner, fence.OperationId, fence.LeaseOwner, fence.FencingVersion, Lease);
        renewal.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);

        _harness.Time.Advance(TimeSpan.FromSeconds(80));

        var retry = await challengerOperations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-renewed", leaseOwner: "worker-retry", lease: Lease));

        retry.Outcome.Should().Be(
            CoachTurnClaimOutcome.InProgress,
            "past the original grant but inside a renewed one, a retry must observe the running turn rather than take it");

        // And the holder is still the writer, which is the point of having renewed.
        var appended = await holderMessages.AppendAsync(
            CoachHistorySamples.Owner,
            Append(CoachHistorySamples.CoachText("The slow answer, delivered once."), fence));

        appended.Status.Should().Be(CoachHistoryStatus.Success);

        var completed = await holderOperations.CompleteAsync(
            CoachHistorySamples.Owner, fence.OperationId, fence.LeaseOwner, fence.FencingVersion, "{}", 1, null, null);
        completed.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);

        (await CoachMessageCountAsync()).Should().Be(1);
        (await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM \"CoachTurnOperation\" WHERE \"AttemptCount\" > 1")).Should().Be(0);
    }

    [PostgresFact]
    public async Task A_worker_that_never_renews_is_still_taken_over_once_its_lease_expires()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var dead = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-dead", leaseOwner: "worker-dead", lease: Lease));
        dead.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        _harness.Time.Advance(Lease + TimeSpan.FromSeconds(1));

        var replacement = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-dead", leaseOwner: "worker-live", lease: Lease));

        replacement.Outcome.Should().Be(
            CoachTurnClaimOutcome.Claimed,
            "renewal must not make a crashed turn permanently unclaimable");
        replacement.Operation!.AttemptCount.Should().Be(2);
    }

    [PostgresFact]
    public async Task Racing_a_takeover_against_a_fenced_append_never_yields_two_answers()
    {
        const int Trials = 8;

        await using var seed = _harness.NewContext();
        var seedOperations = _harness.NewTurnOperationStore(seed);
        var conversations = _harness.NewConversationStore(seed);

        for (var trial = 0; trial < Trials; trial++)
        {
            var created = await conversations.CreateAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.CreateConversation($"Race {trial}"));
            created.Status.Should().Be(CoachHistoryStatus.Success);
            var conversationId = created.Conversation!.Id;

            var claim = await seedOperations.ClaimAsync(
                CoachHistorySamples.Owner,
                CoachHistorySamples.Claim(conversationId, key: $"idem-race-{trial}", leaseOwner: "worker-first", lease: Lease));
            claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

            var staleFence = new CoachTurnFence(claim.Operation!.Id, "worker-first", claim.FencingVersion);

            // The lease has lapsed, so the takeover is legitimate and the stale worker's append is
            // racing it on a separate connection.
            _harness.Time.Advance(Lease + TimeSpan.FromSeconds(1));

            await using var appendingDb = _harness.NewContext();
            await using var claimingDb = _harness.NewContext();
            var appending = _harness.NewMessageStore(appendingDb);
            var claiming = _harness.NewTurnOperationStore(claimingDb);

            using var gate = new Barrier(2);

            var appendTask = Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await AppendOrFaultAsync(
                    appending,
                    Append(CoachHistorySamples.CoachText("Answer from the stale worker."), staleFence),
                    conversationId);
            });

            var claimTask = Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await claiming.ClaimAsync(
                    CoachHistorySamples.Owner,
                    CoachHistorySamples.Claim(conversationId, key: $"idem-race-{trial}", leaseOwner: "worker-second", lease: Lease));
            });

            var appendStatus = await appendTask;
            var takeover = await claimTask;

            takeover.Outcome.Should().BeOneOf(
                CoachTurnClaimOutcome.Claimed,
                CoachTurnClaimOutcome.InProgress,
                CoachTurnClaimOutcome.ConversationBusy);

            // Whichever way the two serialized, the stale worker got at most one line in and can
            // never get another: the takeover is committed and its fence no longer matches.
            var written = await CoachMessageCountAsync(conversationId);
            written.Should().BeLessThanOrEqualTo(1);

            if (appendStatus != CoachHistoryStatus.Success)
            {
                written.Should().Be(0);
            }

            var afterwards = await AppendOrFaultAsync(
                appending,
                Append(CoachHistorySamples.CoachText("Second answer from the stale worker."), staleFence),
                conversationId);

            if (takeover.Outcome == CoachTurnClaimOutcome.Claimed)
            {
                afterwards.Should().Be(
                    CoachHistoryStatus.LeaseLost,
                    "once the takeover is committed the superseded fence is refused every time");
            }

            (await CoachMessageCountAsync(conversationId)).Should().BeLessThanOrEqualTo(1);
        }
    }

    /// <summary>
    /// Runs an append and reports its status, treating a lock timeout as a refusal.
    /// </summary>
    /// <remarks>
    /// A blocked fence is a refusal that has not finished waiting yet. It matters that the write
    /// did not land, not which layer said no, and folding the two into one status keeps the
    /// assertions about the transcript rather than about Npgsql error codes. The provider's
    /// execution strategy wraps the failure, so the whole inner chain is inspected rather than
    /// only the exception that surfaced.
    /// </remarks>
    private static async Task<CoachHistoryStatus> AppendOrFaultAsync(
        CoachMessageStore messages,
        AppendCoachMessageRequest request,
        string? conversationId = null)
    {
        var target = conversationId is null ? request : request with { ConversationId = conversationId };

        try
        {
            var result = await messages.AppendAsync(CoachHistorySamples.Owner, target);
            return result.Status;
        }
        catch (Exception ex) when (IsLockTimeout(ex))
        {
            return CoachHistoryStatus.LeaseLost;
        }
    }

    private static bool IsLockTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable })
            {
                return true;
            }
        }

        return false;
    }

    private AppendCoachMessageRequest Append(CoachMessagePayload payload, CoachTurnFence fence) =>
        new(
            _conversationId,
            CoachMessageRole.Coach,
            CoachMessageKind.Text,
            payload,
            fence.OperationId,
            MessageId: null,
            Fence: fence);

    private async Task<long> CoachMessageCountAsync(string? conversationId = null)
    {
        await using var db = _harness.NewContext();
        return await db.CoachMessages
            .AsNoTracking()
            .CountAsync(m => m.ConversationId == (conversationId ?? _conversationId)
                          && m.Role == CoachMessageRole.Coach);
    }
}
