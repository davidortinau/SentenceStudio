using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The durable turn: the ledger it writes, the idempotency it guarantees, the single writer it
/// enforces, and what survives when the process does not.
/// </summary>
public class CoachDurableTurnTests
{
    [Fact]
    public async Task A_turn_writes_the_learner_message_before_the_model_is_called()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        IReadOnlyList<CoachMessageRecord> duringModelCall = Array.Empty<CoachMessageRecord>();
        harness.Coach.OnRun = async _ => duringModelCall = await harness.LedgerAsync(conversationId);

        await harness.TurnAsync(conversationId, "How do I say hello?");

        // The learner's own words are durable before anything uncertain happens. A model that
        // never answers must not also swallow what the learner typed.
        duringModelCall.Should().ContainSingle();
        duringModelCall[0].Payload!.Kind.Should().Be(CoachMessagePayloadKind.LearnerText);
        duringModelCall[0].Payload!.Text.Should().Be("How do I say hello?");
    }

    [Fact]
    public async Task A_completed_turn_appends_the_coach_reply_after_the_learner_message()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Reply("Say 안녕하세요.");
        var result = await harness.TurnAsync(conversationId, "How do I greet someone?");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.State.Should().Be(CoachTurnOperationState.Completed);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Select(m => m.Payload!.Kind).Should().Equal(
            CoachMessagePayloadKind.LearnerText,
            CoachMessagePayloadKind.CoachText);

        ledger.Select(m => m.Sequence).Should().BeInAscendingOrder("sequence is the only ordering to trust");
        ledger[1].Payload!.Text.Should().Be("Say 안녕하세요.");
    }

    [Fact]
    public async Task The_same_key_with_the_same_request_replays_without_a_second_model_call()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Reply("Once only.");

        var first = await harness.TurnAsync(conversationId, "Tell me once", idempotencyKey: "turn-1");
        var second = await harness.TurnAsync(conversationId, "Tell me once", idempotencyKey: "turn-1");

        first.IsOk.Should().BeTrue(first.Detail);
        second.IsOk.Should().BeTrue(second.Detail);

        second.Value!.OperationId.Should().Be(first.Value!.OperationId);
        harness.Coach.RunCount.Should().Be(1, "a replay must not call the model again");

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2, "a replay must not append a second copy of the turn");
    }

    [Fact]
    public async Task A_replay_reconstructs_the_same_public_response()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Reply("Deterministic answer.");

        var first = await harness.TurnAsync(conversationId, "Ask", idempotencyKey: "turn-replay");
        var second = await harness.TurnAsync(conversationId, "Ask", idempotencyKey: "turn-replay");

        second.Value!.Result.Should().NotBeNull("the stored outcome is what a retry gets back");
        second.Value.Result!.Messages.Select(m => m.Text).Should()
            .Equal(first.Value!.Result!.Messages.Select(m => m.Text));
        second.Value.FirstResponseSequence.Should().Be(first.Value.FirstResponseSequence);
        second.Value.LastResponseSequence.Should().Be(first.Value.LastResponseSequence);
    }

    [Fact]
    public async Task The_same_key_with_a_different_request_is_refused()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await harness.TurnAsync(conversationId, "First question", idempotencyKey: "turn-2");
        var conflicting = await harness.TurnAsync(conversationId, "Completely different", idempotencyKey: "turn-2");

        conflicting.IsOk.Should().BeFalse();
        conflicting.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);
        conflicting.ProblemType.Should().Be(CoachProblemTypes.IdempotencyConflict);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().NotContain(m => m.Payload!.Text == "Completely different",
            "a refused turn must leave nothing behind");
    }

    [Fact]
    public async Task A_turn_requires_an_idempotency_key()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.Service.SubmitTurnAsync(conversationId, new CoachConversationTurnRequest
        {
            IdempotencyKey = "",
            Turn = new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "Hello" }
        });

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        harness.Coach.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task A_second_writer_is_refused_while_one_turn_holds_the_conversation()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        CoachOperationResult<CoachTurnOperationDto>? concurrent = null;

        // Fired from inside the model call, so the first turn genuinely still holds the lease.
        harness.Coach.OnRun = async _ =>
        {
            harness.Coach.OnRun = null;
            concurrent = await harness.TurnAsync(conversationId, "Me too", idempotencyKey: "second");
        };

        var first = await harness.TurnAsync(conversationId, "Mine first", idempotencyKey: "first");

        first.IsOk.Should().BeTrue(first.Detail);
        concurrent.Should().NotBeNull();
        concurrent!.IsOk.Should().BeFalse();
        concurrent.Status.Should().Be(CoachOperationStatus.RunInProgress);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().NotContain(m => m.Payload!.Text == "Me too",
            "the losing writer never got as far as the ledger");
    }

    [Fact]
    public async Task A_turn_against_another_learners_conversation_is_not_found()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.ActAs(CoachConversationHarness.OtherUserId);
        var result = await harness.TurnAsync(conversationId, "Let me in");

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        harness.Coach.RunCount.Should().Be(0, "ownership is settled before a model is ever reached");
    }

    [Fact]
    public async Task A_failed_model_keeps_the_learner_message_and_records_a_failed_operation()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.App.AgentFactory.IsModelAvailable = false;

        var result = await harness.TurnAsync(conversationId, "Please answer", idempotencyKey: "fails");

        result.IsOk.Should().BeFalse();

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().ContainSingle("the learner's words survive a failed turn");
        ledger[0].Payload!.Text.Should().Be("Please answer");

        var operations = await harness.Operations.GetAsync(harness.Owner, ledger[0].OperationId!);
        operations.Should().NotBeNull();
        operations!.Status.Should().Be(CoachTurnOperationStatus.Failed);
        operations.ErrorCode.Should().NotBeNullOrWhiteSpace();
        operations.ErrorCode.Should().NotContain("Please answer", "an error code never carries learner text");
    }

    [Fact]
    public async Task A_failed_turn_can_be_retried_under_a_new_key_and_succeeds()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.App.AgentFactory.IsModelAvailable = false;
        await harness.TurnAsync(conversationId, "Try once", idempotencyKey: "attempt-1");

        harness.App.AgentFactory.IsModelAvailable = true;
        harness.Coach.NextResult = Reply("Recovered.");
        var retried = await harness.TurnAsync(conversationId, "Try once", idempotencyKey: "attempt-2");

        retried.IsOk.Should().BeTrue(retried.Detail);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Where(m => m.Payload!.Kind == CoachMessagePayloadKind.CoachText).Should().ContainSingle(
            "only the successful attempt produced a reply");
    }

    [Fact]
    public async Task An_operation_can_be_polled_after_a_restart_and_still_returns_its_result()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Reply("Durable across a restart.");
        var submitted = await harness.TurnAsync(conversationId, "Will this survive?", idempotencyKey: "restart-1");
        submitted.IsOk.Should().BeTrue(submitted.Detail);

        harness.Restart();

        var polled = await harness.Service.GetOperationAsync(conversationId, submitted.Value!.OperationId);

        polled.IsOk.Should().BeTrue(polled.Detail);
        polled.Value!.State.Should().Be(CoachTurnOperationState.Completed);
        polled.Value.Result.Should().NotBeNull("the durable outcome is stored, not held in memory");
        polled.Value.Result!.Messages.Should().ContainSingle(m => m.Text == "Durable across a restart.");
        polled.Value.Messages.Should().HaveCount(1);
        polled.Value.Messages[0].Message.Text.Should().Be("Durable across a restart.");
    }

    [Fact]
    public async Task A_retry_after_a_restart_produces_exactly_one_effect()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Reply("Only once.");
        await harness.TurnAsync(conversationId, "Do the thing", idempotencyKey: "exactly-once");

        harness.Restart();

        var replayed = await harness.TurnAsync(conversationId, "Do the thing", idempotencyKey: "exactly-once");

        replayed.IsOk.Should().BeTrue(replayed.Detail);
        harness.Coach.RunCount.Should().Be(1, "the restart must not turn a retry into a second run");

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_operation_belonging_to_another_conversation_is_not_found()
    {
        using var harness = new CoachConversationHarness();
        var first = await harness.CreateConversationAsync(idempotencyKey: "c1");
        var second = await harness.CreateConversationAsync(idempotencyKey: "c2");

        var submitted = await harness.TurnAsync(first, "Hello");
        submitted.IsOk.Should().BeTrue(submitted.Detail);

        var crossed = await harness.Service.GetOperationAsync(second, submitted.Value!.OperationId);

        crossed.IsOk.Should().BeFalse();
        crossed.Status.Should().Be(CoachOperationStatus.SessionNotFound,
            "the route's conversation id is never authority over the operation's own");
    }

    [Fact]
    public async Task Another_learner_cannot_poll_an_operation()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        var submitted = await harness.TurnAsync(conversationId, "Private");

        harness.ActAs(CoachConversationHarness.OtherUserId);
        var polled = await harness.Service.GetOperationAsync(conversationId, submitted.Value!.OperationId);

        polled.IsOk.Should().BeFalse();
        polled.Status.Should().Be(CoachOperationStatus.SessionNotFound);
    }

    /// <summary>
    /// A completed turn, with the serialized agent session a real arm always returns alongside it.
    /// </summary>
    /// <remarks>
    /// The session blob is not decoration. A completed run always serializes its <c>AgentSession</c>
    /// — <c>CoachAgentTurnRunner</c> only returns null when the agent produced no state at all — and
    /// the presence of that blob is what tells the next turn whether it has agent memory to resume
    /// or has to seed itself from the ledger. A fixture that always answered with null state would
    /// make every turn look like a rebuild and would hide the case where a checkpoint was cleared
    /// underneath a live conversation.
    /// </remarks>
    internal static CoachAgentTurnResult Reply(string text) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = text
        },
        AgentSessionJson =
            """{"conversationId":"_agent_local_chat_history","stateBag":{"reply":"""
            + System.Text.Json.JsonSerializer.Serialize(text)
            + "}}"
    };
}
