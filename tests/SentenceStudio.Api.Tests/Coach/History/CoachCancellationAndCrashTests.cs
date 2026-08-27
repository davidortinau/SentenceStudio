using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Cancellation, and the crash windows a durable turn has to survive.
/// </summary>
/// <remarks>
/// Both subjects are the same question asked twice: when a turn stops partway through, does the
/// learner end up with one effect, no effect, or an honest account of a half-effect — and never
/// two effects or a silent one.
/// </remarks>
public sealed class CoachCancellationAndCrashTests
{
    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task A_cancel_that_lands_while_the_model_is_running_stops_the_turn_before_it_is_applied()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        // The learner hits cancel while the model is still thinking. The request goes to the
        // database from another request thread; the running turn has to notice it there.
        harness.Coach.OnRun = async _ =>
        {
            var operationId = await harness.LatestOperationIdAsync(conversationId);
            await harness.Operations.RequestCancelAsync(harness.Owner, operationId!);
        };

        var result = await harness.TurnAsync(conversationId, "Actually, never mind");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.State.Should().Be(CoachTurnOperationState.Cancelled);
    }

    [Fact]
    public async Task A_cancelled_turn_keeps_the_learner_message_and_adds_a_visible_notice()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = async _ =>
        {
            var operationId = await harness.LatestOperationIdAsync(conversationId);
            await harness.Operations.RequestCancelAsync(harness.Owner, operationId!);
        };

        await harness.TurnAsync(conversationId, "Half a thought");

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2);
        ledger[0].Role.Should().Be(CoachMessageRole.Learner);
        ledger[0].Payload.Text.Should().Be("Half a thought", "withdrawing a turn does not unsay it");
        ledger[1].Role.Should().Be(CoachMessageRole.Coach);
        ledger[1].Payload.Kind.Should().Be(CoachMessagePayloadKind.Notice);
    }

    [Fact]
    public async Task A_cancelled_turn_changes_no_plan_and_no_settings()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        // A session has to exist before there is any plan state to compare.
        await harness.TurnAsync(conversationId, "Set us up");
        var before = await harness.App.Service.GetSessionAsync(conversationId);

        harness.Coach.OnRun = async _ =>
        {
            var operationId = await harness.LatestOperationIdAsync(conversationId);
            await harness.Operations.RequestCancelAsync(harness.Owner, operationId!);
        };

        await harness.TurnAsync(conversationId, "Change everything");

        var after = await harness.App.Service.GetSessionAsync(conversationId);
        after.Value!.PlanState.Should().BeEquivalentTo(before.Value!.PlanState);
    }

    [Fact]
    public async Task A_durable_cancel_is_honoured_by_a_replica_that_never_saw_the_request()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        // First attempt dies mid-model, leaving a live operation behind.
        harness.Coach.OnRun = _ => throw new InvalidOperationException("Replica died.");
        var key = Guid.NewGuid().ToString("N");
        var crash = () => harness.TurnAsync(conversationId, "Withdrawn", key);
        await crash.Should().ThrowAsync<InvalidOperationException>();

        await harness.SimulateProcessDeathAsync(conversationId);
        var operationId = await harness.LatestOperationIdAsync(conversationId);
        await harness.Operations.RequestCancelAsync(harness.Owner, operationId!);

        // A second replica picks the work up after the lease lapses. It has no in-memory record
        // of the cancel; the only place it can learn about it is the database.
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Coach.OnRun = null;
        harness.Coach.Requests.Clear();
        harness.Restart();
        harness.ActAs(CoachConversationHarness.OwnerUserId);

        var result = await harness.TurnAsync(conversationId, "Withdrawn", key);

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.State.Should().Be(CoachTurnOperationState.Cancelled);
        harness.Coach.Requests.Should().BeEmpty("a withdrawn turn is not sent to the model by the next replica");
    }

    [Fact]
    public async Task Cancelling_a_completed_operation_does_not_rewrite_its_outcome()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        var done = await harness.TurnAsync(conversationId, "Finished already");
        done.Value!.State.Should().Be(CoachTurnOperationState.Completed);

        var cancel = await harness.Service.CancelOperationAsync(conversationId, done.Value.OperationId);

        cancel.IsOk.Should().BeTrue(cancel.Detail);
        cancel.Value!.State.Should().Be(
            CoachTurnOperationState.Completed,
            "a turn that already happened cannot be un-happened by asking");
    }

    [Fact]
    public async Task Cancelling_another_learners_operation_is_refused()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();
        var turn = await harness.TurnAsync(conversationId, "Mine");

        harness.ActAs(CoachConversationHarness.OtherUserId);
        var cancel = await harness.Service.CancelOperationAsync(conversationId, turn.Value!.OperationId);

        cancel.IsOk.Should().BeFalse();
        cancel.Status.Should().Be(CoachOperationStatus.SessionNotFound);
    }

    // ---------------------------------------------------------------- crash windows

    [Fact]
    public async Task A_crash_while_appending_the_learner_message_leaves_nothing_behind()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        harness.FaultingMessages.FailOnAppendNumber = 1;
        var key = Guid.NewGuid().ToString("N");

        var act = () => harness.TurnAsync(conversationId, "Died on the way in", key);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await harness.LedgerAsync(conversationId)).Should().BeEmpty();
        harness.Coach.Requests.Should().BeEmpty("the model is not asked before the learner's words are safe");
    }

    [Fact]
    public async Task Retrying_after_a_crash_before_the_model_produces_exactly_one_effect()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = _ => throw new InvalidOperationException("Process died mid-model.");
        var key = Guid.NewGuid().ToString("N");

        var first = () => harness.TurnAsync(conversationId, "Retry me", key);
        await first.Should().ThrowAsync<InvalidOperationException>();

        await harness.SimulateProcessDeathAsync(conversationId);
        harness.Coach.OnRun = null;
        harness.Restart();
        harness.ActAs(CoachConversationHarness.OwnerUserId);

        var retry = await harness.TurnAsync(conversationId, "Retry me", key);

        retry.IsOk.Should().BeTrue(retry.Detail);
        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Count(m => m.Role == CoachMessageRole.Learner)
            .Should().Be(1, "a retried turn says the learner's line once, not twice");
        ledger.Count(m => m.Payload.Kind == CoachMessagePayloadKind.CoachText)
            .Should().Be(1, "and it produces one answer, not one per attempt");
    }

    [Fact]
    public async Task A_crash_after_the_model_but_before_the_reply_is_stored_is_recovered_on_retry()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        // Append 1 is the learner message; append 2 is the coach reply. Die on the reply.
        harness.FaultingMessages.FailOnAppendNumber = 2;
        var key = Guid.NewGuid().ToString("N");

        var crash = () => harness.TurnAsync(conversationId, "Answer me", key);
        await crash.Should().ThrowAsync<InvalidOperationException>();

        await harness.SimulateProcessDeathAsync(conversationId);
        harness.FaultingMessages.FailOnAppendNumber = null;
        harness.Restart();
        harness.ActAs(CoachConversationHarness.OwnerUserId);

        var retry = await harness.TurnAsync(conversationId, "Answer me", key);

        retry.IsOk.Should().BeTrue(retry.Detail);
        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Count(m => m.Role == CoachMessageRole.Learner).Should().Be(1);
        ledger.Should().Contain(m => m.Role == CoachMessageRole.Coach, "the learner ends up with an answer");
    }

    [Fact]
    public async Task A_crash_before_the_operation_is_completed_still_answers_the_retry_from_the_ledger()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        // Everything commits — learner message, model call, coach reply — and then the process
        // dies before the operation row is marked done. This is the worst window: the work is
        // real but the record of it is missing.
        harness.FaultingOperations.FailOnComplete = true;
        var key = Guid.NewGuid().ToString("N");

        var crash = () => harness.TurnAsync(conversationId, "Committed but unrecorded", key);
        await crash.Should().ThrowAsync<InvalidOperationException>();

        var committed = await harness.LedgerAsync(conversationId);
        committed.Should().Contain(m => m.Role == CoachMessageRole.Coach);

        await harness.SimulateProcessDeathAsync(conversationId);
        harness.FaultingOperations.FailOnComplete = false;
        harness.Restart();
        harness.ActAs(CoachConversationHarness.OwnerUserId);

        var retry = await harness.TurnAsync(conversationId, "Committed but unrecorded", key);

        retry.IsOk.Should().BeTrue(retry.Detail);
        retry.Value!.State.Should().Be(CoachTurnOperationState.Completed);

        var after = await harness.LedgerAsync(conversationId);
        after.Should().HaveCount(
            committed.Count,
            "recovery reads what the dead attempt committed instead of doing the work again");
        harness.Coach.Requests.Should().HaveCount(1, "the model is not asked a second time for work already done");
    }

    [Fact]
    public async Task A_lost_http_response_is_replayed_byte_for_byte_on_retry()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        var key = Guid.NewGuid().ToString("N");

        // The turn fully succeeds and the response never reaches the client.
        var lost = await harness.TurnAsync(conversationId, "Did you get that?", key);
        lost.IsOk.Should().BeTrue(lost.Detail);

        harness.Restart();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var replay = await harness.TurnAsync(conversationId, "Did you get that?", key);

        replay.IsOk.Should().BeTrue(replay.Detail);
        replay.Value!.OperationId.Should().Be(lost.Value!.OperationId);
        replay.Value.Result.Should().BeEquivalentTo(lost.Value.Result);
        harness.Coach.Requests.Should().HaveCount(1);
        (await harness.LedgerAsync(conversationId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_model_failure_keeps_the_learner_message_and_records_a_failed_operation()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = _ => throw new InvalidOperationException("Model unavailable.");
        var key = Guid.NewGuid().ToString("N");

        var crash = () => harness.TurnAsync(conversationId, "Nobody answered", key);
        await crash.Should().ThrowAsync<InvalidOperationException>();

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().ContainSingle().Which.Role.Should().Be(CoachMessageRole.Learner);

        var operationId = await harness.LatestOperationIdAsync(conversationId);
        var operation = await harness.Operations.GetAsync(harness.Owner, operationId!);
        operation.Should().NotBeNull();
        operation!.Status.Should().NotBe(CoachTurnOperationStatus.Completed);
    }
}
