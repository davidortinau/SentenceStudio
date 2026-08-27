using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The lease has to outlive the work it authorizes.
/// </summary>
/// <remarks>
/// <para>
/// A turn holds the single-writer slot for a conversation, and it holds it by lease so that a
/// crashed worker cannot block the conversation forever. Those two facts only stay consistent
/// while somebody is renewing: a lease granted once and never extended turns into a deadline on
/// the turn itself, and any turn that runs past it is still working on a conversation another
/// worker is now entitled to claim.
/// </para>
/// <para>
/// The failure that produces is not a crash. Both workers finish, both append an answer, and the
/// learner sees the same turn answered twice — while the first worker only finds out it lost at
/// the very last step, long after its output is already in the transcript. These tests pin the
/// two properties that prevent it: the lease is renewed for as long as the work lasts, and every
/// durable write is admitted only against a fencing token the winner has not superseded.
/// </para>
/// </remarks>
public sealed class CoachTurnLeaseRenewalTests
{
    /// <summary>The lease the durable turn path takes, mirrored so the tests can reason in it.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Renewal = CoachTurnLeaseHeartbeat.RenewalInterval(Lease);

    /// <summary>Real-time bound on waiting for a virtual renewal, so a missing one fails rather than hangs.</summary>
    private static readonly TimeSpan RenewalDeadlockGuard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_turn_that_runs_past_its_lease_keeps_the_lease_alive_while_it_works()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        DateTime? grantedUntil = null;
        DateTime? heldUntil = null;

        harness.Coach.OnRun = async _ =>
        {
            grantedUntil = await LeaseExpiryAsync(harness, conversationId);

            // Four renewal intervals is longer than the original grant, so a turn this slow
            // would previously have been running on a lease that had already lapsed.
            await AdvanceThroughRenewalsAsync(harness, 4);

            heldUntil = await LeaseExpiryAsync(harness, conversationId);
        };

        var result = await harness.TurnAsync(conversationId, "Can we do more listening?");

        result.IsOk.Should().BeTrue(result.Detail);
        harness.Renewer.Attempts.Should().Be(4);
        harness.Renewer.Outcomes.Should().OnlyContain(o => o == CoachTurnFinalizeOutcome.Success);

        grantedUntil.Should().NotBeNull();
        heldUntil.Should().NotBeNull();
        heldUntil!.Value.Should().BeAfter(
            grantedUntil!.Value,
            "a live worker must push its lease forward, or the conversation becomes claimable while it is still writing");

        // The elapsed run is longer than the lease it was granted, which is the whole point: the
        // turn survived a window that used to end with another worker taking the conversation.
        (Renewal * 4).Should().BeGreaterThan(Lease);
    }

    [Fact]
    public async Task A_retry_arriving_while_the_lease_is_renewed_is_told_the_turn_is_still_running()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        const string Key = "idem-slow-turn";
        const string OperationId = "op-slow-turn";
        const string LearnerText = "Can we do more listening?";

        CoachTurnClaimOutcome? retryOutcome = null;

        harness.Coach.OnRun = async _ =>
        {
            await AdvanceThroughRenewalsAsync(harness, 4);

            // A second worker — another replica, or the same one serving the learner's retry —
            // asks for the conversation on its own database context, which is what a retry
            // actually looks like from the store's point of view.
            await using var db = harness.App.Persistence.NewContext();
            var operations = harness.App.Persistence.NewTurnOperationStore(db);

            var retry = await operations.ClaimAsync(
                harness.Owner,
                new ClaimCoachTurnRequest(
                    conversationId,
                    Key,
                    CanonicalRequestFor(conversationId, LearnerText),
                    "worker-retry",
                    Lease,
                    OperationId));

            retryOutcome = retry.Outcome;
        };

        var result = await harness.TurnAsync(
            conversationId, LearnerText, idempotencyKey: Key, operationId: OperationId);

        result.IsOk.Should().BeTrue(result.Detail);

        retryOutcome.Should().Be(
            CoachTurnClaimOutcome.InProgress,
            "a renewed lease means the first attempt is alive, so the retry must wait for it rather than take it over");

        harness.Coach.RunCount.Should().Be(1, "the model must not be asked the same question twice");

        var operation = await harness.Db.CoachTurnOperations
            .AsNoTracking()
            .SingleAsync(o => o.ConversationId == conversationId);
        operation.AttemptCount.Should().Be(1, "nobody took the turn over");
        operation.FencingVersion.Should().Be(1);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Count(m => m.Role == CoachMessageRole.Coach).Should().Be(1, "one turn, one answer");
    }

    [Fact]
    public async Task Losing_the_lease_stops_the_run_and_refuses_to_report_success()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = async _ =>
        {
            // Whatever the cause — a takeover, a purged row, a clock that ran away — the renewal
            // comes back saying this worker is no longer the writer.
            harness.Renewer.ForcedOutcome = CoachTurnFinalizeOutcome.LeaseLost;

            await AdvanceThroughRenewalsAsync(harness, 1);
        };

        var result = await harness.TurnAsync(conversationId, "Swap the reading for listening.");

        result.IsOk.Should().BeFalse("a superseded worker has no answer to give");
        result.Status.Should().Be(CoachOperationStatus.RunInProgress);
        result.ProblemType.Should().Be(CoachProblemTypes.RunInProgress);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().NotContain(
            m => m.Role == CoachMessageRole.Coach,
            "a worker that lost the lease must not put an answer in the transcript");

        // The learner's own line was written before the lease went, and it stays: losing a lease
        // is not a reason to forget what somebody typed.
        ledger.Should().ContainSingle(m => m.Role == CoachMessageRole.Learner);
    }

    [Fact]
    public async Task A_superseded_worker_cannot_append_a_learner_visible_message()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await using var db = harness.App.Persistence.NewContext();
        var operations = harness.App.Persistence.NewTurnOperationStore(db);
        var messages = harness.App.Persistence.NewMessageStore(db);

        var first = await operations.ClaimAsync(
            harness.Owner,
            new ClaimCoachTurnRequest(conversationId, "idem-fence", "payload", "worker-first", Lease));
        first.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var staleFence = new CoachTurnFence(first.Operation!.Id, "worker-first", first.FencingVersion);

        // The first worker stops renewing — a crash, a pause, a network partition — and the lease
        // lapses, so a second worker takes the conversation over.
        harness.Time.Advance(Lease + TimeSpan.FromSeconds(1));

        var second = await operations.ClaimAsync(
            harness.Owner,
            new ClaimCoachTurnRequest(conversationId, "idem-fence", "payload", "worker-second", Lease));
        second.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);
        second.FencingVersion.Should().BeGreaterThan(staleFence.FencingVersion);

        // The first worker now finishes the model call it started before it stalled and tries to
        // publish its answer. This is the exact write that used to land.
        var stale = await messages.AppendAsync(
            harness.Owner,
            new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Coach,
                CoachMessageKind.Text,
                CoachHistorySamples.CoachText("Answer from the worker that was replaced."),
                staleFence.OperationId,
                MessageId: null,
                Fence: staleFence));

        stale.Status.Should().Be(
            CoachHistoryStatus.LeaseLost,
            "the fencing token is checked by the same statement that admits the write");
        stale.Message.Should().BeNull();

        // And the winner's write is admitted, once.
        var winning = await messages.AppendAsync(
            harness.Owner,
            new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Coach,
                CoachMessageKind.Text,
                CoachHistorySamples.CoachText("Answer from the worker that holds the lease."),
                second.Operation!.Id,
                MessageId: null,
                Fence: new CoachTurnFence(second.Operation.Id, "worker-second", second.FencingVersion)));

        winning.Status.Should().Be(CoachHistoryStatus.Success);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().ContainSingle(
            m => m.Role == CoachMessageRole.Coach,
            "exactly one answer reached the learner");
    }

    [Fact]
    public async Task A_fenced_write_is_refused_once_the_operation_has_reached_a_terminal_state()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await using var db = harness.App.Persistence.NewContext();
        var operations = harness.App.Persistence.NewTurnOperationStore(db);
        var messages = harness.App.Persistence.NewMessageStore(db);

        var claim = await operations.ClaimAsync(
            harness.Owner,
            new ClaimCoachTurnRequest(conversationId, "idem-terminal", "payload", "worker-a", Lease));
        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var fence = new CoachTurnFence(claim.Operation!.Id, "worker-a", claim.FencingVersion);

        var failed = await operations.FailAsync(
            harness.Owner, claim.Operation.Id, "worker-a", claim.FencingVersion, "turn_failed");
        failed.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);

        var late = await messages.AppendAsync(
            harness.Owner,
            new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Coach,
                CoachMessageKind.Text,
                CoachHistorySamples.CoachText("Late answer for a turn that already ended."),
                fence.OperationId,
                MessageId: null,
                Fence: fence));

        late.Status.Should().Be(
            CoachHistoryStatus.LeaseLost,
            "an operation that has already been answered for holds no lease to write under");
    }

    [Fact]
    public async Task A_worker_that_stops_renewing_can_still_be_taken_over_once_its_lease_expires()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await using var db = harness.App.Persistence.NewContext();
        var operations = harness.App.Persistence.NewTurnOperationStore(db);

        var dead = await operations.ClaimAsync(
            harness.Owner,
            new ClaimCoachTurnRequest(conversationId, "idem-crash", "payload", "worker-dead", Lease));
        dead.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        // No renewal at all, which is what a killed process looks like from the database.
        harness.Time.Advance(Lease + TimeSpan.FromSeconds(1));

        var replacement = await operations.ClaimAsync(
            harness.Owner,
            new ClaimCoachTurnRequest(conversationId, "idem-crash", "payload", "worker-live", Lease));

        replacement.Outcome.Should().Be(
            CoachTurnClaimOutcome.Claimed,
            "renewal must not make a crashed turn unrecoverable — the lease still lapses when nobody holds it");
        replacement.FencingVersion.Should().BeGreaterThan(dead.FencingVersion);

        var stale = await operations.CompleteAsync(
            harness.Owner, dead.Operation!.Id, "worker-dead", dead.FencingVersion, "{}", 1, null, null);
        stale.Outcome.Should().Be(CoachTurnFinalizeOutcome.LeaseLost);
    }

    [Fact]
    public async Task A_renewal_that_cannot_reach_the_database_holds_the_lease_it_already_has()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = async _ =>
        {
            // The database is unreachable, but the grant this worker already holds is still valid
            // for most of a lease. Surrendering on the first failed renewal would abandon turns
            // over a blip; never surrendering would let a worker with no lease keep writing.
            harness.Renewer.ForcedFault = () => new InvalidOperationException("no connection");

            await AdvanceThroughRenewalsAsync(harness, 1);
        };

        var result = await harness.TurnAsync(conversationId, "Add one more reading passage.");

        result.IsOk.Should().BeTrue(
            "one failed renewal inside a still-valid lease is not a reason to throw the turn away");
        harness.Renewer.Attempts.Should().Be(1);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().Contain(m => m.Role == CoachMessageRole.Coach);
    }

    [Fact]
    public async Task A_renewal_outage_that_outlasts_the_lease_surrenders_rather_than_writing()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = async _ =>
        {
            harness.Renewer.ForcedFault = () => new InvalidOperationException("no connection");

            // Three intervals reaches the end of the grant this worker was given, so by the last
            // one the lease has genuinely run out and another worker may already own the
            // conversation. There is no fourth attempt: a surrendered heartbeat stops renewing.
            await AdvanceThroughRenewalsAsync(harness, 3);
        };

        var result = await harness.TurnAsync(conversationId, "Swap the reading for listening.");

        result.IsOk.Should().BeFalse("the lease this worker held has expired and it cannot prove it still owns the turn");
        result.Status.Should().Be(CoachOperationStatus.RunInProgress);
        harness.Renewer.Attempts.Should().Be(3, "renewal stops once the lease is surrendered");

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().NotContain(m => m.Role == CoachMessageRole.Coach);
    }

    /// <summary>
    /// Moves the virtual clock through <paramref name="count"/> renewal intervals, waiting for
    /// each renewal to actually land before moving again.
    /// </summary>
    /// <remarks>
    /// The clock is virtual; the wait is not. A heartbeat tick starts a renewal and returns, so
    /// advancing time only proves the renewal was due. The real-time bound is a deadlock guard,
    /// not a timing assumption: a build in which nothing renews fails here with a timeout instead
    /// of hanging the suite, which is exactly what these tests are for.
    /// </remarks>
    private static async Task AdvanceThroughRenewalsAsync(CoachConversationHarness harness, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var renewed = harness.Renewer.WaitForNextAsync();
            harness.Time.Advance(Renewal);
            await renewed.WaitAsync(RenewalDeadlockGuard);
        }
    }

    private static async Task<DateTime?> LeaseExpiryAsync(CoachConversationHarness harness, string conversationId)
    {
        await using var db = harness.App.Persistence.NewContext();
        return await db.CoachTurnOperations
            .AsNoTracking()
            .Where(o => o.ConversationId == conversationId)
            .OrderByDescending(o => o.FencingVersion)
            .Select(o => o.LeaseExpiresAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// The canonical request bytes the durable turn path digests, mirrored so a test can play the
    /// part of a retry that presents the same payload rather than a conflicting one.
    /// </summary>
    private static string CanonicalRequestFor(string conversationId, string text) =>
        string.Join(
            '\u001f',
            conversationId,
            CoachTurnInputKind.Text.ToString(),
            text,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
}
