using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The heartbeat must not race the write that ends the turn it is keeping alive.
/// </summary>
/// <remarks>
/// <para>
/// Renewing a lease and finalizing an operation are two writes to the same row, made from two
/// different database contexts, and the finalizing one reads the row before it writes it. A
/// renewal that commits inside that window moves the row's concurrency token, so the finalizing
/// write is rejected — not because this worker was superseded, but because it lost a race with its
/// own heartbeat.
/// </para>
/// <para>
/// The cost of that refusal is not an error the learner sees. It is a success: the old code
/// reported the turn as OK and handed back the copy of the row it had read <em>before</em> the
/// completion, which still said Running. The client merged the answer, then polled that row for
/// four minutes and timed out on a turn that had actually worked. These tests pin both halves of
/// the fix — the heartbeat is stopped before the row is finalized, and a finalizing write that is
/// still refused never comes back as a success carrying a Running row.
/// </para>
/// </remarks>
public sealed class CoachTurnFinalizationRaceTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Renewal = CoachTurnLeaseHeartbeat.RenewalInterval(Lease);

    [Fact]
    public async Task The_heartbeat_is_stopped_before_the_operation_is_completed()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var attemptsBefore = -1;
        var attemptsInside = -1;

        harness.FaultingOperations.BeforeComplete = () =>
        {
            attemptsBefore = harness.Renewer.Attempts;

            // Answered without touching the database from here on: what is being measured is
            // whether the heartbeat still fires, not whether its write would land.
            harness.Renewer.ForcedOutcome = CoachTurnFinalizeOutcome.Success;

            // Three intervals. A heartbeat that is still running ticks on every one of them, and
            // each tick is a renewal aimed at the row this completion is about to write.
            harness.Time.Advance(Renewal * 3);

            attemptsInside = harness.Renewer.Attempts;
        };

        var result = await harness.TurnAsync(conversationId, "Swap the reading for listening.");

        result.IsOk.Should().BeTrue(result.Detail);

        attemptsInside.Should().Be(
            attemptsBefore,
            "a renewal that starts while the turn is being completed is exactly the write that refuses the completion");

        harness.Renewer.Attempts.Should().Be(
            attemptsBefore,
            "nothing may renew the lease on an operation that has reached a terminal state");
    }

    [Fact]
    public async Task The_heartbeat_is_stopped_before_a_cancelled_turn_is_finalized()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = async _ =>
        {
            var operationId = await harness.LatestOperationIdAsync(conversationId);
            await harness.Operations.RequestCancelAsync(harness.Owner, operationId!);
        };

        var attemptsBefore = -1;
        var attemptsInside = -1;

        harness.FaultingOperations.BeforeFail = () =>
        {
            attemptsBefore = harness.Renewer.Attempts;
            harness.Renewer.ForcedOutcome = CoachTurnFinalizeOutcome.Success;
            harness.Time.Advance(Renewal * 3);
            attemptsInside = harness.Renewer.Attempts;
        };

        var result = await harness.TurnAsync(conversationId, "Actually, never mind");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.State.Should().Be(CoachTurnOperationState.Cancelled);

        attemptsInside.Should().Be(
            attemptsBefore,
            "cancelling writes the same row as completing, and loses the same race to a renewal");
    }

    [Fact]
    public async Task A_renewal_that_commits_inside_the_completion_still_leaves_the_turn_completed()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        using var renewal = RenewalDuring(harness, CoachTurnOperationStatus.Completed);

        var result = await harness.TurnAsync(conversationId, "Can we do more listening?");

        renewal.Injections.Should().Be(1, "the race this test exists for has to have happened");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.State.Should().Be(
            CoachTurnOperationState.Completed,
            "a Running answer is what left the client polling a row nobody would ever move");

        var operation = await harness.Db.CoachTurnOperations
            .AsNoTracking()
            .SingleAsync(o => o.ConversationId == conversationId);

        operation.Status.Should().Be(
            CoachTurnOperationStatus.Completed,
            "the durable record has to agree with the answer the client was given");
        operation.LeaseExpiresAt.Should().BeNull("a finished operation holds no lease");
        operation.LeaseOwner.Should().BeNull();

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Count(m => m.Role == CoachMessageRole.Coach).Should().Be(1, "one turn, one answer");

        harness.Renewer.Attempts.Should().Be(
            0,
            "the turn finished inside its first lease, and nothing renews a lease after the turn is over");
    }

    [Fact]
    public async Task A_renewal_that_commits_inside_a_cancellation_still_leaves_the_turn_cancelled()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = async _ =>
        {
            var operationId = await harness.LatestOperationIdAsync(conversationId);
            await harness.Operations.RequestCancelAsync(harness.Owner, operationId!);
        };

        using var renewal = RenewalDuring(harness, CoachTurnOperationStatus.Cancelled);

        var result = await harness.TurnAsync(conversationId, "Actually, never mind");

        renewal.Injections.Should().Be(1);

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.State.Should().Be(
            CoachTurnOperationState.Cancelled,
            "a cancelled turn reported as Running is the same four-minute poll as a completed one");

        var operation = await harness.Db.CoachTurnOperations
            .AsNoTracking()
            .SingleAsync(o => o.ConversationId == conversationId);

        operation.Status.Should().Be(CoachTurnOperationStatus.Cancelled);
        operation.LeaseExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task A_completion_that_cannot_be_settled_is_reported_as_a_conflict_rather_than_a_success()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        // A refusal the store cannot resolve by re-reading — the shape a genuinely contended row
        // ends in once the retries are spent. The turn's own writes are already committed; only
        // the record saying the turn is over is missing.
        harness.FaultingOperations.CompleteOutcome =
            CoachTurnFinalizeResult.Failed(CoachTurnFinalizeOutcome.Conflict);

        var result = await harness.TurnAsync(conversationId, "Swap the reading for listening.");

        result.IsOk.Should().BeFalse(
            "reporting success with a Running row is what made a working turn look like a four-minute hang");
        result.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);
        result.ProblemType.Should().Be(CoachProblemTypes.ConversationStateConflict);
        result.ProblemType.Should().NotBe(
            CoachProblemTypes.RunInProgress,
            "a run-in-progress answer sends the client to poll the very row that is stuck");
    }

    /// <summary>
    /// Commits a lease renewal from outside the finalizing unit of work, in the window between the
    /// finalizing read and the finalizing write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DbContext.SavingChanges"/> is the seam: it is raised after the operation row has
    /// been read and mutated and before the update is sent, which is precisely the window a
    /// heartbeat renewal used to land in. The write goes out as raw SQL rather than through the
    /// store because what matters is what a renewal <em>does</em> to the row — it moves the
    /// concurrency token — not which code path moved it.
    /// </para>
    /// <para>
    /// Injected once. The finalizing write re-reads and retries after losing the race, and a
    /// second injection would be a different test: an unbounded writer, not a heartbeat.
    /// </para>
    /// </remarks>
    private static RenewalInjection RenewalDuring(
        CoachConversationHarness harness,
        CoachTurnOperationStatus finalizingTo) =>
        new(harness, finalizingTo);

    private sealed class RenewalInjection : IDisposable
    {
        private readonly CoachConversationHarness _harness;
        private readonly CoachTurnOperationStatus _finalizingTo;

        public RenewalInjection(CoachConversationHarness harness, CoachTurnOperationStatus finalizingTo)
        {
            _harness = harness;
            _finalizingTo = finalizingTo;
            _harness.Db.SavingChanges += OnSavingChanges;
        }

        public int Injections { get; private set; }

        public void Dispose() => _harness.Db.SavingChanges -= OnSavingChanges;

        private void OnSavingChanges(object? sender, SavingChangesEventArgs e)
        {
            if (Injections > 0)
            {
                return;
            }

            var finalizing = _harness.Db.ChangeTracker
                .Entries<CoachTurnOperation>()
                .FirstOrDefault(entry => entry.State == EntityState.Modified
                                      && entry.Entity.Status == _finalizingTo);

            if (finalizing is null)
            {
                return;
            }

            Injections++;
            Renew(finalizing);
        }

        private void Renew(EntityEntry<CoachTurnOperation> finalizing)
        {
            using var command = _harness.App.Persistence.NewRawCommand(
                """
                UPDATE "CoachTurnOperation"
                SET "Version" = "Version" + 1
                WHERE "Id" = @id
                """);

            command.Parameters.Add(new SqliteParameter("@id", finalizing.Entity.Id));
            command.ExecuteNonQuery().Should().Be(1, "the operation row a renewal would extend has to exist");
        }
    }
}
