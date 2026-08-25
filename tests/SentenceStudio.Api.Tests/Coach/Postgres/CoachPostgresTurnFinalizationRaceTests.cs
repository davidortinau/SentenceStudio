using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// A lease renewal landing between a completion's read and its write, against a real PostgreSQL
/// server.
/// </summary>
/// <remarks>
/// <para>
/// This is the race the lease heartbeat introduced. Completing an operation reads the row, decides
/// the caller may write it, and then writes it under the row's concurrency token. The heartbeat
/// renews that same row from a different context on a different connection, and a renewal that
/// commits between those two steps moves the token — so the completion is rejected, and the turn
/// that answered correctly is left recorded as Running with a lease nobody will renew again. The
/// client merges the answer it was handed, polls that row, and gives up after four minutes.
/// </para>
/// <para>
/// Only a real server can decide this. Two contexts over one SQLite handle take turns, so the
/// concurrency token never moves under a read; two PostgreSQL connections under read committed do
/// exactly what production does. The interleaving is forced rather than hoped for: the renewal is
/// issued from a command interceptor on the finalizing context, immediately after the finalizing
/// SELECT has executed and before its UPDATE is sent.
/// </para>
/// </remarks>
public sealed class CoachPostgresTurnFinalizationRaceTests : IAsyncLifetime
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

        _harness = await CoachPostgresHarness.CreateAsync("finalize-race");

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
    public async Task A_renewal_that_commits_between_the_completion_read_and_its_write_still_completes_the_turn()
    {
        // The renewal runs on its own context, exactly as the heartbeat's scoped renewer does.
        await using var renewing = _harness.NewContext();
        var renewals = _harness.NewTurnOperationStore(renewing);

        CoachTurnFence fence = null!;
        var renewalOutcome = (CoachTurnFinalizeOutcome?)null;

        var interceptor = new RenewBetweenReadAndWrite(async () =>
            renewalOutcome = (await renewals.RenewLeaseAsync(
                CoachHistorySamples.Owner,
                fence.OperationId,
                fence.LeaseOwner,
                fence.FencingVersion,
                Lease)).Outcome);

        await using var finalizing = NewInterceptedContext(interceptor);
        var operations = new CoachTurnOperationStore(
            finalizing,
            _harness.ContentProtector,
            _harness.Time,
            NullLogger<CoachTurnOperationStore>.Instance);
        var messages = _harness.NewMessageStore(finalizing);

        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-finalize-race", leaseOwner: "worker-live", lease: Lease));
        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        fence = new CoachTurnFence(claim.Operation!.Id, "worker-live", claim.FencingVersion);

        // The turn's visible output is committed before the completion is attempted, which is what
        // makes a refused completion so expensive: the answer is already in the transcript.
        var appended = await messages.AppendAsync(
            CoachHistorySamples.Owner,
            new AppendCoachMessageRequest(
                _conversationId,
                CoachMessageRole.Coach,
                CoachMessageKind.Text,
                CoachHistorySamples.CoachText("The answer the learner is waiting for."),
                fence.OperationId,
                MessageId: null,
                Fence: fence));
        appended.Status.Should().Be(CoachHistoryStatus.Success);

        interceptor.Arm();

        var completed = await operations.CompleteAsync(
            CoachHistorySamples.Owner,
            fence.OperationId,
            fence.LeaseOwner,
            fence.FencingVersion,
            "{}",
            1,
            appended.Message!.Sequence,
            appended.Message.Sequence);

        interceptor.Injections.Should().Be(1, "the race this test exists for has to have happened");
        renewalOutcome.Should().Be(
            CoachTurnFinalizeOutcome.Success,
            "the injected write has to be a real renewal that moved the row, not a no-op");

        completed.Outcome.Should().Be(
            CoachTurnFinalizeOutcome.Success,
            "losing a race with this worker's own heartbeat is not a reason to refuse its completion");

        // What a polling client reads. Running here is the four-minute hang.
        await using var polling = _harness.NewContext();
        var reader = _harness.NewTurnOperationStore(polling);

        var polled = await reader.GetAsync(CoachHistorySamples.Owner, fence.OperationId);
        polled!.Status.Should().Be(
            CoachTurnOperationStatus.Completed,
            "the first poll must find the turn finished rather than waiting out its budget on a row nobody will move");
        polled.LeaseExpiresAt.Should().BeNull("a finished operation holds no lease");
        polled.LeaseOwner.Should().BeNull();

        (await reader.GetOutcomeAsync(CoachHistorySamples.Owner, fence.OperationId))
            .Should().NotBeNull("the replayable outcome is what a client that lost its response reads back");

        (await CoachMessageCountAsync()).Should().Be(1, "one turn, one answer");
    }

    [PostgresFact]
    public async Task Nothing_renews_the_lease_once_the_operation_is_terminal()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var claim = await operations.ClaimAsync(
            CoachHistorySamples.Owner,
            CoachHistorySamples.Claim(_conversationId, key: "idem-after-terminal", leaseOwner: "worker-live", lease: Lease));
        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var completed = await operations.CompleteAsync(
            CoachHistorySamples.Owner, claim.Operation!.Id, "worker-live", claim.FencingVersion, "{}", 1, null, null);
        completed.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);

        // A renewal that arrives after the turn ended — a tick that was already in flight when the
        // heartbeat was stopped, in a build that stopped it too late.
        await using var renewing = _harness.NewContext();
        var late = await _harness.NewTurnOperationStore(renewing).RenewLeaseAsync(
            CoachHistorySamples.Owner, claim.Operation.Id, "worker-live", claim.FencingVersion, Lease);

        late.Outcome.Should().Be(
            CoachTurnFinalizeOutcome.AlreadyTerminal,
            "a finished operation has no lease to extend, and handing it one would make it look claimable again");

        (await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM \"CoachTurnOperation\" WHERE \"LeaseExpiresAt\" IS NOT NULL"))
            .Should().Be(0, "no terminal operation is left holding a lease");
    }

    private CoachDbContext NewInterceptedContext(RenewBetweenReadAndWrite interceptor) =>
        new(new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql(_harness.ConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(CoachPostgresHarness.CoachMigrationsHistoryTable))
            .AddInterceptors(interceptor)
            .Options);

    private async Task<long> CoachMessageCountAsync()
    {
        await using var db = _harness.NewContext();
        return await db.CoachMessages
            .AsNoTracking()
            .CountAsync(m => m.ConversationId == _conversationId && m.Role == CoachMessageRole.Coach);
    }

    /// <summary>
    /// Commits a lease renewal in the window between a finalizing read and its write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A command interceptor rather than a background task racing on a timer, because "sometimes
    /// interleaves" is not a test. The renewal is issued the moment the finalizing SELECT has
    /// executed, which is the window a heartbeat tick would have had to hit by luck, and it commits
    /// on a different connection before the finalizing UPDATE is sent.
    /// </para>
    /// <para>
    /// Armed explicitly and injected once, so the claim's own queries and the finalizing write's
    /// re-read after it loses the race are left alone: what is being modelled is a heartbeat with a
    /// bounded tick rate, not a writer that never stops.
    /// </para>
    /// </remarks>
    private sealed class RenewBetweenReadAndWrite : DbCommandInterceptor
    {
        private readonly Func<Task> _renew;
        private bool _armed;

        public RenewBetweenReadAndWrite(Func<Task> renew) => _renew = renew;

        public int Injections { get; private set; }

        public void Arm() => _armed = true;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (_armed
                && Injections == 0
                && command.CommandText.Contains("\"CoachTurnOperation\"", StringComparison.Ordinal))
            {
                Injections++;
                await _renew().ConfigureAwait(false);
            }

            return result;
        }
    }
}
