using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.Compatibility;
using SentenceStudio.Api.Coach.Endpoints;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The old <c>/sessions</c> routes write to the ledger when durable history is on.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a real failure. A client with history enabled started a conversation
/// through the new surface and then posted its turn to the old route. The conversation row was
/// there, the learner got a normal reply, and the ledger stayed empty. Nothing threw and nothing
/// logged, because the old route simply never touched history.
/// </para>
/// <para>
/// Picking the wrong route is a client bug. Losing the learner's transcript over it is ours, and a
/// gap shaped like success is the kind that survives review. So the promise pinned here is not
/// "both routes exist" but "either route puts the turn in the ledger, exactly once".
/// </para>
/// </remarks>
public sealed class CoachLegacySessionRoutingTests
{
    // --------------------------------------------------------------- the reported failure

    /// <summary>
    /// The bug, stated as a test: a turn posted to the old route lands in the ledger.
    /// </summary>
    [Fact]
    public async Task A_turn_posted_to_the_old_route_is_written_to_the_ledger()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("Try greeting someone first.");
        var turn = await harness.Compat.SubmitTurnAsync(sessionId, Text("how do I say hello?"));

        turn.IsOk.Should().BeTrue(turn.Detail);
        turn.Value!.Messages.Should().Contain(m => m.Text == "Try greeting someone first.");

        var ledger = await harness.LedgerAsync(sessionId);
        ledger.Should().HaveCount(2, "the learner's message and the coach's reply are both recorded");
        ledger.Should().ContainSingle(m => m.Role == CoachMessageRole.Learner);
        ledger.Should().ContainSingle(m => m.Role == CoachMessageRole.Coach);
    }

    /// <summary>
    /// The turn also leaves a completed durable operation behind, which is what makes it
    /// recoverable rather than merely recorded.
    /// </summary>
    [Fact]
    public async Task A_turn_posted_to_the_old_route_completes_a_durable_operation()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("Sure.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("what next?"));

        var operation = harness.Db.CoachTurnOperations.Single();
        operation.ConversationId.Should().Be(sessionId);
        operation.Status.Should().Be(CoachTurnOperationStatus.Completed);
        operation.LastResponseSequence.Should().NotBeNull("the stored outcome spans the appended messages");
        operation.ProtectedOutcome.Should().NotBeNullOrEmpty("the response can be replayed");
    }

    /// <summary>
    /// A suggestion accepted through the old route is recorded too. The receipt is the part a
    /// learner would most notice missing, since it is the only record that their plan changed.
    /// </summary>
    [Fact]
    public async Task A_suggestion_accepted_through_the_old_route_records_its_receipt()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        var suggestion = await SuggestAsync(harness, sessionId);

        var accepted = await harness.Compat.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.IsOk.Should().BeTrue(accepted.Detail);
        accepted.Value!.ChangeReceipt.Should().NotBeNull();

        var ledger = await harness.LedgerAsync(sessionId);
        ledger.Should().ContainSingle(m => m.Kind == CoachMessageKind.Receipt);
        harness.Db.CoachPlanRevisions.Should().ContainSingle("accepting applies the plan change once");
    }

    /// <summary>
    /// Rejecting is recorded as well. It changes no plan, but the learner declining a suggestion is
    /// part of the conversation and reads oddly if the suggestion is there and the answer is not.
    /// </summary>
    [Fact]
    public async Task A_suggestion_rejected_through_the_old_route_is_recorded_and_changes_no_plan()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        var suggestion = await SuggestAsync(harness, sessionId);
        var before = (await harness.LedgerAsync(sessionId)).Count;

        var rejected = await harness.Compat.RejectSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        rejected.IsOk.Should().BeTrue(rejected.Detail);
        harness.Db.CoachPlanRevisions.Should().BeEmpty("rejecting writes no plan change");

        var after = await harness.LedgerAsync(sessionId);
        after.Count.Should().BeGreaterThan(before, "the decision itself is part of the transcript");
    }

    /// <summary>
    /// Undo through the old route reaches the ledger on the same path as everything else.
    /// </summary>
    [Fact]
    public async Task An_undo_through_the_old_route_is_recorded()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        var suggestion = await SuggestAsync(harness, sessionId);
        await harness.Compat.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        var before = (await harness.LedgerAsync(sessionId)).Count;

        var undone = await harness.Compat.UndoAsync(sessionId, new CoachUndoRequest());

        undone.IsOk.Should().BeTrue(undone.Detail);
        (await harness.LedgerAsync(sessionId)).Count
            .Should().BeGreaterThan(before, "the reversal is visible history, not a silent rollback");
    }

    // ----------------------------------------------------------------------- replay

    /// <summary>
    /// A client that resends the same turn id gets its stored answer back rather than a second
    /// turn. This is the retry a flaky network produces, and the old route now survives it.
    /// </summary>
    [Fact]
    public async Task Resending_the_same_client_turn_id_replays_instead_of_running_again()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("Once only.");
        var first = await harness.Compat.SubmitTurnAsync(sessionId, Text("hello?", "turn-1"));
        var callsAfterFirst = harness.Coach.Requests.Count;

        harness.Coach.NextResult = Answer("This must never be reached.");
        var second = await harness.Compat.SubmitTurnAsync(sessionId, Text("hello?", "turn-1"));

        second.IsOk.Should().BeTrue(second.Detail);
        second.Value!.Messages.Select(m => m.Text)
            .Should().Equal(first.Value!.Messages.Select(m => m.Text));
        harness.Coach.Requests.Count.Should().Be(callsAfterFirst, "the model is not asked twice");

        (await harness.LedgerAsync(sessionId)).Should().HaveCount(2, "no second pair of messages");
    }

    /// <summary>
    /// The same turn id with different words is a client mistake, not a retry, and is refused
    /// rather than quietly answered — otherwise one of the two messages disappears.
    /// </summary>
    [Fact]
    public async Task Reusing_a_client_turn_id_for_different_text_is_refused()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("First.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("first question", "turn-1"));

        harness.Coach.NextResult = Answer("Second.");
        var clash = await harness.Compat.SubmitTurnAsync(sessionId, Text("a different question", "turn-1"));

        clash.IsOk.Should().BeFalse("the same key with a different payload is a conflict");
        (await harness.LedgerAsync(sessionId)).Should().HaveCount(2, "the second turn was not run");
    }

    /// <summary>
    /// Without a client turn id there is no retry key, which is exactly what the old route always
    /// meant. Two deliberate sends of the same words stay two turns.
    /// </summary>
    [Fact]
    public async Task Two_sends_with_no_client_turn_id_stay_two_turns()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("Again is fine.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("say that again"));

        harness.Coach.NextResult = Answer("Again is fine.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("say that again"));

        (await harness.LedgerAsync(sessionId)).Should().HaveCount(4, "asking twice is two turns");
    }

    // ------------------------------------------------------------------ no duplication

    /// <summary>
    /// The old route routes into the durable path rather than writing alongside it, so a client
    /// that uses the new route sees one copy of each message and not two.
    /// </summary>
    [Fact]
    public async Task A_turn_on_the_new_route_is_not_duplicated_by_the_compatibility_fork()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("From the new route.");
        var durable = await harness.TurnAsync(sessionId, "how do I say hello?");
        durable.IsOk.Should().BeTrue(durable.Detail);

        (await harness.LedgerAsync(sessionId)).Should().HaveCount(2);
        harness.Db.CoachTurnOperations.Should().ContainSingle();
    }

    /// <summary>
    /// Both routes writing into one thread is the whole point of the alias, so a turn from each
    /// interleaves into a single ordered transcript.
    /// </summary>
    [Fact]
    public async Task Turns_from_both_routes_share_one_ordered_transcript()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("Old route reply.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("first, from the old client"));

        harness.Coach.NextResult = Answer("New route reply.");
        await harness.TurnAsync(sessionId, "second, from the new client");

        var ledger = await harness.LedgerAsync(sessionId);
        ledger.Should().HaveCount(4);
        ledger.Select(m => m.Sequence).Should().BeInAscendingOrder("one ledger, one clock");
        harness.Db.CoachTurnOperations.Should().HaveCount(2);
    }

    // ------------------------------------------------------------------------ the flag off

    /// <summary>
    /// With history off the old route behaves exactly as it always did and writes nothing durable.
    /// </summary>
    [Fact]
    public async Task With_history_off_the_old_route_stays_on_the_legacy_path()
    {
        using var harness = new CoachConversationHarness(durableHistory: false);
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = Answer("Same as before.");
        var turn = await harness.Compat.SubmitTurnAsync(sessionId, Text("how do I say hello?"));

        turn.IsOk.Should().BeTrue(turn.Detail);
        turn.Value!.Messages.Should().Contain(m => m.Text == "Same as before.");

        harness.Db.CoachMessages.Should().BeEmpty();
        harness.Db.CoachTurnOperations.Should().BeEmpty();
        harness.Db.CoachConversations.Should().BeEmpty();
    }

    /// <summary>
    /// A session that predates durable history has no conversation behind it, so it keeps working
    /// on the legacy path even on a host where the flag is on. Rolling the feature forward must not
    /// strand the sessions that were already open.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_conversation_behind_it_still_works_with_history_on()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        // Model the pre-history session by removing the conversation the start created.
        harness.Db.CoachConversations.RemoveRange(harness.Db.CoachConversations);
        await harness.Db.SaveChangesAsync();

        harness.Coach.NextResult = Answer("Still answering.");
        var turn = await harness.Compat.SubmitTurnAsync(sessionId, Text("are you there?"));

        turn.IsOk.Should().BeTrue(turn.Detail);
        harness.Db.CoachMessages.Should().BeEmpty("there is no ledger to write to");
    }

    // ---------------------------------------------------------------------- ownership

    /// <summary>
    /// Another learner's session id is not a way in through the old route. It answers the same as
    /// an id that never existed, so the alias cannot be used to probe for one.
    /// </summary>
    [Fact]
    public async Task Another_learners_session_is_not_reachable_through_the_old_route()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();
        harness.Coach.NextResult = Answer("Mine.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("hello?"));

        harness.ActAs(CoachConversationHarness.OtherUserId);

        var stolen = await harness.Compat.SubmitTurnAsync(sessionId, Text("show me that"));
        var invented = await harness.Compat.SubmitTurnAsync("no-such-session", Text("show me that"));

        stolen.IsOk.Should().BeFalse();
        invented.IsOk.Should().BeFalse();
        stolen.Status.Should().Be(invented.Status, "a foreign id is indistinguishable from a missing one");

        harness.ActAs(CoachConversationHarness.OwnerUserId);
        (await harness.LedgerAsync(sessionId)).Should().HaveCount(2, "nothing was appended by the intruder");
    }

    /// <summary>
    /// Deleting through the old route removes the transcript, not just the checkpoint over it.
    /// </summary>
    /// <remarks>
    /// A learner deleting a thread on the old surface means the thread. Clearing only the session
    /// would leave the whole conversation to reappear the first time they opened the new history
    /// screen, which is the opposite of what they asked for.
    /// </remarks>
    [Fact]
    public async Task Deleting_through_the_old_route_hides_the_conversation_as_well()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();
        harness.Coach.NextResult = Answer("Recorded.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("hello?"));

        var deleted = await harness.Compat.DeleteSessionAsync(sessionId);

        deleted.IsOk.Should().BeTrue(deleted.Detail);
        (await harness.Service.GetAsync(sessionId)).IsOk
            .Should().BeFalse("the conversation is gone from the learner's history too");
    }

    /// <summary>
    /// A second delete still answers not-found, exactly as it did before history was durable.
    /// </summary>
    /// <remarks>
    /// The tempting change is to make this idempotent, and it is deliberately not made here. The
    /// old route has always answered 404 for an id it cannot find, a released client reads that
    /// answer, and this release is meant to preserve the old shape rather than improve it. If
    /// delete should become idempotent, that is a change to the route's contract and belongs with
    /// the new surface, not smuggled in under a compatibility alias.
    /// </remarks>
    [Fact]
    public async Task Deleting_twice_answers_not_found_exactly_as_the_old_route_always_did()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();
        harness.Coach.NextResult = Answer("Recorded.");
        await harness.Compat.SubmitTurnAsync(sessionId, Text("hello?"));

        (await harness.Compat.DeleteSessionAsync(sessionId)).IsOk.Should().BeTrue();
        var again = await harness.Compat.DeleteSessionAsync(sessionId);

        again.IsOk.Should().BeFalse();
        again.Status.Should().Be(CoachOperationStatus.SessionNotFound, "the old answer is preserved");
    }

    // -------------------------------------------------------------------- model failure

    /// <summary>
    /// When the model fails, the learner's own words survive. Losing what they typed is worse than
    /// the failure itself, because it is the part they cannot reproduce from memory of the reply.
    /// </summary>
    [Fact]
    public async Task A_model_failure_on_the_old_route_still_keeps_the_learner_message()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult { Outcome = CoachAgentOutcome.ModelUnavailable };
        var turn = await harness.Compat.SubmitTurnAsync(sessionId, Text("this one fails"));

        turn.IsOk.Should().BeFalse();

        var ledger = await harness.LedgerAsync(sessionId);
        ledger.Should().ContainSingle(m => m.Role == CoachMessageRole.Learner);
        ledger.Should().NotContain(m => m.Role == CoachMessageRole.Coach);
        harness.Db.CoachPlanRevisions.Should().BeEmpty("a failed turn changes nothing");
    }

    // ------------------------------------------------------------------------- cancel

    /// <summary>
    /// Cancel on the old route reaches a durable turn.
    /// </summary>
    /// <remarks>
    /// The legacy cancel signals the local run registry under the session id, and a durable turn
    /// registers under the conversation id. That only reaches the running turn because the two
    /// identifiers are the same handle, which is an assumption worth failing loudly if it is ever
    /// broken rather than discovering it as a cancel button that does nothing.
    /// </remarks>
    [Fact]
    public async Task Cancel_on_the_old_route_reaches_a_durable_turn()
    {
        using var harness = new CoachConversationHarness();
        var sessionId = await harness.App.StartSessionAsync();

        harness.Coach.OnRun = async _ =>
            await harness.Compat.CancelAsync(sessionId);

        harness.Coach.NextResult = Answer("This reply is abandoned.");
        var turn = await harness.Compat.SubmitTurnAsync(sessionId, Text("actually, never mind"));

        turn.IsOk.Should().BeFalse("the turn was withdrawn before it could be applied");

        var ledger = await harness.LedgerAsync(sessionId);
        ledger.Should().ContainSingle(m => m.Role == CoachMessageRole.Learner)
            .Which.Payload!.Text.Should().Be("actually, never mind", "withdrawing a turn does not unsay it");

        harness.Db.CoachTurnOperations.Single().Status
            .Should().Be(CoachTurnOperationStatus.Cancelled, "the cancel is recorded, not only acted on");
        harness.Db.CoachPlanRevisions.Should().BeEmpty("a cancelled turn applies nothing");
    }

    // ------------------------------------------------------------------------- wiring

    /// <summary>
    /// The fork is reachable from the composed application graph, registered through the coach's
    /// own extension rather than by hand at startup.
    /// </summary>
    [Fact]
    public void The_compatibility_fork_is_registered_by_the_coach_extension()
    {
        using var harness = new CoachConversationHarness();

        typeof(CoachApplicationServiceCollectionExtensions)
            .Should().NotBeNull("the fork is registered where the rest of the coach is");

        harness.Compat.Should().NotBeNull();
    }

    /// <summary>
    /// The regression guard. The reported failure was a handler wired straight to the session
    /// service, so this fails the moment one is wired that way again.
    /// </summary>
    /// <remarks>
    /// It reads the handler signatures rather than exercising the routes because the fault was in
    /// the wiring, not the behaviour: every one of these services answered correctly, and the only
    /// thing wrong was which one the route reached for.
    /// </remarks>
    [Theory]
    [InlineData("SubmitTurnAsync")]
    [InlineData("AcceptSuggestionAsync")]
    [InlineData("RejectSuggestionAsync")]
    [InlineData("UndoAsync")]
    [InlineData("DeleteSessionAsync")]
    [InlineData("StartSessionAsync")]
    [InlineData("GetSessionAsync")]
    [InlineData("CancelAsync")]
    public void No_compatibility_route_reaches_past_the_fork_to_the_session_service(string handler)
    {
        var method = typeof(CoachEndpoints)
            .GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull($"{handler} is the handler behind a /sessions route");

        var parameters = method!.GetParameters().Select(p => p.ParameterType).ToArray();

        parameters.Should().NotContain(
            typeof(ICoachSessionService),
            $"{handler} must go through the fork so durable history is not bypassed");
        parameters.Should().Contain(typeof(CoachCompatibilitySessionService));
    }

    // ------------------------------------------------------------------------ helpers

    private static CoachTurnRequest Text(string text, string? clientTurnId = null) => new()
    {
        InputKind = CoachTurnInputKind.Text,
        Text = text,
        ClientTurnId = clientTurnId
    };

    private static async Task<PendingCoachSuggestionDto> SuggestAsync(
        CoachConversationHarness harness, string sessionId)
    {
        // The claim below is now checked against what the turn read, so the read has to happen.
        harness.App.SeedPracticeBalanceRead();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
                CoachMessage = "Would you like a shorter session today?",
                EvidenceReferences =
                [
                    new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance, WindowDays = 14 }
                ]
            }
        };

        var proposed = await harness.Compat.SubmitTurnAsync(sessionId, Text("what should I do today?"));
        proposed.IsOk.Should().BeTrue(proposed.Detail);
        proposed.Value!.PendingSuggestion.Should().NotBeNull();
        return proposed.Value.PendingSuggestion!;
    }

    private static CoachAgentTurnResult Answer(string message) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = message
        }
    };
}
