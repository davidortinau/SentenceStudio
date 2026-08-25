using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The plan and the conversation ledger are written through two different contexts against one
/// physical database, so a turn that changes Today's Plan cannot commit both halves atomically.
/// These tests pin the saga that closes that gap: the durable operation records the intent before
/// the plan moves, and a retry after a crash reconciles from what actually committed rather than
/// running the plan write a second time.
/// </summary>
/// <remarks>
/// The failure these guard against is the expensive one. A learner asks for a shorter session, the
/// plan changes, the process dies before the receipt is written, and the retry applies the change
/// again — so the learner's day shrinks twice for one request. "Exactly one plan write per
/// idempotency key" is the property under test throughout.
/// </remarks>
public sealed class CoachPlanWriteSagaTests
{
    // ------------------------------------------------------------------ the happy path

    /// <summary>
    /// The baseline the crash tests are measured against: one request, one plan write, one
    /// revision, and a receipt the learner can read back out of the ledger.
    /// </summary>
    [Fact]
    public async Task A_plan_change_writes_the_plan_once_and_leaves_a_receipt_in_the_history()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = DirectChange();

        var result = await harness.TurnAsync(conversationId, "make it 10 minutes and no audio");

        result.IsOk.Should().BeTrue(result.Detail);
        harness.App.PlanService.ApplyCallCount.Should().Be(1);
        harness.Db.CoachPlanRevisions.Should().HaveCount(1);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().Contain(
            m => m.Payload.Kind == CoachMessagePayloadKind.Receipt,
            "the learner has to be able to see that their plan changed, and why");
    }

    // ------------------------------------------------------------------ crash after the plan write

    /// <summary>
    /// The window the saga exists for. The plan write commits through the plan context, then the
    /// process dies before the receipt reaches the ledger through the coach context. On retry the
    /// plan must not move again.
    /// </summary>
    [Fact]
    public async Task A_crash_after_the_plan_commits_does_not_apply_the_change_a_second_time()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = DirectChange();

        // Append 1 is the learner's message, which has to survive. Fail on the first coach-side
        // append after it, which lands after the plan service has already been called.
        harness.FaultingMessages.FailOnAppendNumber = 2;

        var key = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.TurnAsync(conversationId, "make it 10 minutes and no audio", key));

        harness.App.PlanService.ApplyCallCount.Should().Be(1, "the plan write got through before the crash");

        // A thrown turn is a graceful failure; rewind the operation row to the state a real
        // process death leaves behind — Running, with a lease nobody holds.
        await harness.SimulateProcessDeathAsync(conversationId);
        harness.FaultingMessages.FailOnAppendNumber = null;
        harness.Restart();

        var retry = await harness.TurnAsync(conversationId, "make it 10 minutes and no audio", key);

        retry.IsOk.Should().BeTrue(retry.Detail);
        harness.App.PlanService.ApplyCallCount.Should().Be(1, "the retry must reconcile, not re-apply");
        harness.Db.CoachPlanRevisions.Should().HaveCount(1, "one request is one revision");
    }

    /// <summary>
    /// Reconciliation is not allowed to be silent, and it is not allowed to be vague. A turn
    /// whose effect landed but whose reply did not has to leave a receipt describing the change,
    /// not a notice admitting one happened.
    /// </summary>
    /// <remarks>
    /// A Notice here would be a regression, not a compromise. The revision is durable and carries
    /// the accepted delta, so the information needed to tell the learner what changed survived
    /// the crash; answering "something was applied" would be discarding an answer we still hold.
    /// </remarks>
    [Fact]
    public async Task A_reconciled_turn_tells_the_learner_the_change_went_through()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = DirectChange();
        harness.FaultingMessages.FailOnAppendNumber = 2;

        var key = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.TurnAsync(conversationId, "make it 10 minutes and no audio", key));

        await harness.SimulateProcessDeathAsync(conversationId);
        harness.FaultingMessages.FailOnAppendNumber = null;
        harness.Restart();

        await harness.TurnAsync(conversationId, "make it 10 minutes and no audio", key);

        var ledger = await harness.LedgerAsync(conversationId);
        var receipt = ledger.Should().ContainSingle(
            m => m.Payload.Kind == CoachMessagePayloadKind.Receipt,
            "a recovered turn reports the change it made, exactly once").Subject;

        receipt.Payload.Receipt!.RevisionId.Should().Be(
            harness.Db.CoachPlanRevisions.Single().Id,
            "the rebuilt receipt points at the revision this turn actually wrote");

        ledger.Should().NotContain(
            m => m.Payload.Kind == CoachMessagePayloadKind.Notice,
            "a committed change is never reported as a notice");
    }

    /// <summary>
    /// The learner's own words are the one thing that must never be lost, whatever else failed.
    /// </summary>
    [Fact]
    public async Task A_crash_mid_turn_keeps_the_learner_message_that_started_it()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = DirectChange();
        harness.FaultingMessages.FailOnAppendNumber = 2;

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.TurnAsync(conversationId, "make it 10 minutes and no audio"));

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().ContainSingle(m => m.Role == CoachMessageRole.Learner);
    }

    // ------------------------------------------------------------------ retries and replays

    /// <summary>
    /// The ordinary retry — same key, same payload, no crash — replays the stored outcome. It must
    /// not reach the plan service at all.
    /// </summary>
    [Fact]
    public async Task Replaying_a_completed_plan_change_does_not_touch_the_plan()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = DirectChange();

        var key = Guid.NewGuid().ToString("N");
        await harness.TurnAsync(conversationId, "make it 10 minutes and no audio", key);
        harness.App.PlanService.ApplyCallCount.Should().Be(1);

        var replay = await harness.TurnAsync(conversationId, "make it 10 minutes and no audio", key);

        replay.IsOk.Should().BeTrue(replay.Detail);
        harness.App.PlanService.ApplyCallCount.Should().Be(1);
        harness.Db.CoachPlanRevisions.Should().HaveCount(1);
    }

    /// <summary>
    /// A model failure is not a plan event. Nothing may move, and the learner is told rather than
    /// left with a turn that looks like it worked.
    /// </summary>
    /// <remarks>
    /// A model that fails cleanly is handled, not thrown: the reducer turns it into a visible
    /// notice and the operation completes carrying that notice. The durable <c>Failed</c>
    /// operation is the unhandled path, which the crash tests cover.
    /// </remarks>
    [Fact]
    public async Task A_model_failure_leaves_the_plan_untouched()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult { Outcome = CoachAgentOutcome.Failed };

        var result = await harness.TurnAsync(conversationId, "make it 10 minutes and no audio");

        harness.App.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().ContainSingle(m => m.Role == CoachMessageRole.Learner, "the learner's message survives");
        ledger.Should().Contain(
            m => m.Payload.Kind == CoachMessagePayloadKind.Notice,
            "a failed turn has to say so out loud");
        result.IsOk.Should().BeTrue(result.Detail);
    }

    /// <summary>
    /// A suggestion is a preview, not a write. It has to be readable in the history without ever
    /// having reached the plan.
    /// </summary>
    [Fact]
    public async Task A_suggested_change_is_recorded_without_writing_the_plan()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
                CoachMessage = "Would a shorter session suit you today?"
            }
        };

        var result = await harness.TurnAsync(conversationId, "I am short on time");

        result.IsOk.Should().BeTrue(result.Detail);
        harness.App.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    /// <summary>
    /// Two conversations belonging to one learner share a single plan, so a change made in one is
    /// a change the other is working against. Each request still gets its own durable turn, and
    /// neither conversation's ledger may pick up the other's messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where the saga meets contention rather than crashes. The property that has to hold
    /// is separation: one request writes the plan once, and the record of it lands in the
    /// conversation that asked, never in the other one.
    /// </para>
    /// <para>
    /// Known gap, deliberately not asserted here: the second conversation's turn applies the plan
    /// change but records a generic notice instead of a change receipt, because the second
    /// conversation is acting on a plan snapshot the first conversation already moved. The plan
    /// write is correct and single; the ledger entry for it is weaker than it should be. Fixing
    /// the receipt path is reducer work in the session service and is reported rather than
    /// silently pinned as intended behaviour.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_conversations_over_one_plan_keep_their_histories_separate()
    {
        using var harness = new CoachConversationHarness();
        var first = await harness.CreateConversationAsync();
        var second = await harness.CreateConversationAsync();

        harness.Coach.NextResult = DirectChange();
        await harness.TurnAsync(first, "make it 10 minutes and no audio");

        harness.Coach.NextResult = DirectChange(minutes: 15);
        await harness.TurnAsync(second, "make it 15 minutes and no audio");

        harness.App.PlanService.ApplyCallCount.Should().Be(2, "two separate requests are two changes");

        var firstLedger = await harness.LedgerAsync(first);
        var secondLedger = await harness.LedgerAsync(second);

        firstLedger.Should().ContainSingle(m => m.Payload.Kind == CoachMessagePayloadKind.Receipt);
        firstLedger.Should().OnlyContain(m => m.ConversationId == first);
        secondLedger.Should().OnlyContain(m => m.ConversationId == second);
        secondLedger.Should().ContainSingle(m => m.Role == CoachMessageRole.Learner);
    }

    /// <summary>
    /// A recovered turn is attributed to the revision it wrote, never to a revision an earlier
    /// turn in the same conversation wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case the previous time-window search got wrong. Recovery looked for revisions
    /// created since the operation started, within the conversation. A turn that committed
    /// nothing would still find the previous turn's revision sitting inside that window and
    /// report it as though this turn had caused it.
    /// </para>
    /// <para>
    /// The second turn here is crashed before the model runs, so it has no revision of its own to
    /// find — only its predecessor's, which is exactly the row that must not be borrowed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_recovered_turn_never_claims_an_earlier_turns_revision()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = DirectChange(minutes: 10);
        await harness.TurnAsync(conversationId, "make it 10 minutes and no audio");

        var earlier = harness.Db.CoachPlanRevisions.Single();
        earlier.OperationId.Should().NotBeNullOrWhiteSpace(
            "a revision written by a durable turn carries the operation that caused it");

        // Crashes on the learner append, so this turn never reaches the model and can commit
        // nothing. Its predecessor's revision is moments old and well inside any plausible window.
        var key = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = DirectChange(minutes: 20);
        harness.FaultingMessages.FailOnNextAppend(1);
        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.TurnAsync(conversationId, "make it 20 minutes and no audio", key));
        await harness.SimulateProcessDeathAsync(conversationId);

        harness.FaultingMessages.FailOnAppendNumber = null;
        harness.Restart();
        harness.Coach.NextResult = DirectChange(minutes: 20);

        var retry = await harness.TurnAsync(conversationId, "make it 20 minutes and no audio", key);
        retry.IsOk.Should().BeTrue(retry.Detail);

        var ledger = await harness.LedgerAsync(conversationId);
        var claimed = ledger
            .Where(m => m.Payload.Kind == CoachMessagePayloadKind.Receipt)
            .Select(m => m.Payload.Receipt!.RevisionId)
            .ToList();

        claimed.Should().OnlyHaveUniqueItems(
            "one revision is reported once, however many turns recovered around it");
        claimed.Count(id => id == earlier.Id).Should().Be(
            1, "the earlier turn's revision belongs to the earlier turn and to nothing else");
    }

    /// <summary>
    /// Two conversations sharing one plan each recover their own change.
    /// </summary>
    /// <remarks>
    /// Both revisions exist at once, so a lookup that resolved by recency rather than by
    /// operation would hand the newer one to whichever conversation asked. The recovery order is
    /// deliberately the reverse of the write order to make that failure visible.
    /// </remarks>
    [Fact]
    public async Task Two_conversations_over_one_plan_each_recover_their_own_change()
    {
        using var harness = new CoachConversationHarness();
        var first = await harness.CreateConversationAsync();

        var firstKey = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = DirectChange(minutes: 10);
        harness.FaultingMessages.FailOnNextAppend(2);
        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.TurnAsync(first, "make it 10 minutes and no audio", firstKey));
        await harness.SimulateProcessDeathAsync(first);

        var firstRevision = harness.Db.CoachPlanRevisions.Single();

        // A second conversation, created after the first plan write, committing its own change.
        var second = await harness.CreateConversationAsync();
        var secondKey = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = DirectChange(minutes: 25);
        harness.FaultingMessages.FailOnNextAppend(2);
        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.TurnAsync(second, "make it 25 minutes and no audio", secondKey));
        await harness.SimulateProcessDeathAsync(second);

        harness.FaultingMessages.FailOnAppendNumber = null;
        harness.Restart();

        // Recovered newest-first, so resolving by recency would give the first conversation the
        // second conversation's revision.
        harness.Coach.NextResult = DirectChange(minutes: 25);
        await harness.TurnAsync(second, "make it 25 minutes and no audio", secondKey);
        harness.Coach.NextResult = DirectChange(minutes: 10);
        await harness.TurnAsync(first, "make it 10 minutes and no audio", firstKey);

        var firstReceipts = (await harness.LedgerAsync(first))
            .Where(m => m.Payload.Kind == CoachMessagePayloadKind.Receipt)
            .Select(m => m.Payload.Receipt!.RevisionId)
            .ToList();

        firstReceipts.Should().Contain(
            firstRevision.Id, "the first conversation reports the change it actually made");

        var secondReceipts = (await harness.LedgerAsync(second))
            .Where(m => m.Payload.Kind == CoachMessagePayloadKind.Receipt)
            .Select(m => m.Payload.Receipt!.RevisionId)
            .ToList();

        secondReceipts.Should().NotContain(
            firstRevision.Id, "a conversation never reports its neighbour's revision as its own");

        harness.Db.CoachPlanRevisions
            .Where(r => r.OperationId != null)
            .Should().OnlyHaveUniqueItems(
                r => r.OperationId!, "one operation writes at most one revision");
    }

    private static CoachAgentTurnResult DirectChange(int minutes = 10) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = minutes, AudioAllowed = false },
            CoachMessage = "Today's Plan now fits the time you have."
        }
    };
}
