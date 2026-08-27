using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// A turn is submitted over a network that can lose the response. These tests pin the property
/// that makes that survivable: the caller names the operation before sending, so a turn whose
/// reply never arrived can still be found and read.
/// </summary>
/// <remarks>
/// <para>
/// The idempotency key cannot serve this purpose. It is salted and hashed before storage, so
/// there is nothing to look up by, and publishing a form that could be looked up would expose the
/// digest the replay check depends on. The two identifiers do different jobs: the key decides
/// whether a request is a repeat, the operation id decides what to ask about.
/// </para>
/// <para>
/// Because the id is chosen by the caller it can also be chosen badly, so these tests cover the
/// collisions a server-allocated id could never have.
/// </para>
/// </remarks>
public sealed class CoachOperationIdentityTests
{
    [Fact]
    public async Task A_turn_requires_an_operation_id()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.Service.SubmitTurnAsync(conversationId, new CoachConversationTurnRequest
        {
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            OperationId = "",
            Turn = new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "Hello" }
        });

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        harness.Coach.RunCount.Should().Be(0, "a turn that cannot be polled for is never started");
    }

    /// <summary>
    /// The id the caller chose is the id the operation keeps.
    /// </summary>
    /// <remarks>
    /// The whole point is that the caller can poll without having seen the response, so an id the
    /// server rewrote — however sensibly — would be useless.
    /// </remarks>
    [Fact]
    public async Task The_operation_is_stored_under_the_id_the_caller_chose()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var chosen = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = Chat();

        var result = await harness.TurnAsync(conversationId, "Hello", operationId: chosen);
        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.OperationId.Should().Be(chosen);

        var stored = await harness.Db.CoachTurnOperations
            .AsNoTracking()
            .SingleAsync(o => o.ConversationId == conversationId);

        stored.Id.Should().Be(chosen);
    }

    /// <summary>
    /// A caller that never saw the response can still read the outcome.
    /// </summary>
    /// <remarks>
    /// This is the scenario the id exists for: the turn ran, the plan may have moved, and the only
    /// thing the caller holds is the name it made up before sending.
    /// </remarks>
    [Fact]
    public async Task A_lost_response_can_still_be_read_by_the_id_the_caller_kept()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var chosen = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = Chat();

        var sent = await harness.TurnAsync(conversationId, "Hello", operationId: chosen);
        sent.IsOk.Should().BeTrue(sent.Detail);

        // Everything the caller knew about the response is discarded; only the id survives.
        var polled = await harness.Service.GetOperationAsync(conversationId, chosen);

        polled.IsOk.Should().BeTrue(polled.Detail);
        polled.Value!.OperationId.Should().Be(chosen);
        polled.Value.State.Should().Be(sent.Value!.State);
        polled.Value.Messages.Should().BeEquivalentTo(
            sent.Value.Messages,
            "polling reconstructs the same public answer the lost response carried");
    }

    /// <summary>
    /// The same id with the same payload is one turn, not two.
    /// </summary>
    [Fact]
    public async Task The_same_id_and_the_same_payload_replays_one_turn()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var chosen = Guid.NewGuid().ToString("N");
        var key = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = Chat();

        var first = await harness.TurnAsync(conversationId, "Hello", key, chosen);
        harness.Coach.NextResult = Chat();
        var second = await harness.TurnAsync(conversationId, "Hello", key, chosen);

        first.IsOk.Should().BeTrue(first.Detail);
        second.IsOk.Should().BeTrue(second.Detail);
        second.Value!.OperationId.Should().Be(first.Value!.OperationId);
        harness.Coach.RunCount.Should().Be(1, "a replay answers from the record, it does not re-run");

        harness.Db.CoachTurnOperations
            .Count(o => o.ConversationId == conversationId)
            .Should().Be(1);
    }

    /// <summary>
    /// The same id with a different payload is a contradiction, and is refused.
    /// </summary>
    /// <remarks>
    /// Answering either way would be wrong: replaying the stored outcome would answer a question
    /// the caller did not ask, and running the new payload would leave two different turns
    /// sharing one name.
    /// </remarks>
    [Fact]
    public async Task The_same_id_with_a_different_payload_is_refused()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var chosen = Guid.NewGuid().ToString("N");
        var key = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = Chat();

        var first = await harness.TurnAsync(conversationId, "Hello", key, chosen);
        first.IsOk.Should().BeTrue(first.Detail);

        harness.Coach.NextResult = Chat();
        var conflicting = await harness.TurnAsync(conversationId, "Something else entirely", key, chosen);

        conflicting.IsOk.Should().BeFalse();
        conflicting.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);
        harness.Coach.RunCount.Should().Be(1, "the contradicting turn never reached the model");
    }

    /// <summary>
    /// Reusing an id under a new idempotency key is refused rather than resolved.
    /// </summary>
    /// <remarks>
    /// The two identifiers now disagree about which turn this is: the key says "never seen", the
    /// id says "already yours". A server that picked a winner would let a client that recycles ids
    /// silently overwrite or shadow its own earlier turn.
    /// </remarks>
    [Fact]
    public async Task Reusing_an_id_under_a_new_key_is_refused()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var chosen = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = Chat();
        var first = await harness.TurnAsync(conversationId, "Hello", Guid.NewGuid().ToString("N"), chosen);
        first.IsOk.Should().BeTrue(first.Detail);

        harness.Coach.NextResult = Chat();
        var reused = await harness.TurnAsync(conversationId, "Hello again", Guid.NewGuid().ToString("N"), chosen);

        reused.IsOk.Should().BeFalse();
        reused.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);

        harness.Db.CoachTurnOperations
            .Count(o => o.ConversationId == conversationId)
            .Should().Be(1, "the refused claim wrote nothing");
    }

    /// <summary>
    /// An id belongs to its owner. Another learner naming it sees nothing.
    /// </summary>
    /// <remarks>
    /// Ids are caller-chosen, so a guessable or copied one must not become a way to read someone
    /// else's turn. The answer is the same as for an id that never existed.
    /// </remarks>
    [Fact]
    public async Task An_operation_id_is_not_readable_by_another_learner()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var chosen = Guid.NewGuid().ToString("N");
        harness.Coach.NextResult = Chat();
        await harness.TurnAsync(conversationId, "Hello", operationId: chosen);

        harness.ActAs("someone-else");

        var polled = await harness.Service.GetOperationAsync(conversationId, chosen);
        polled.IsOk.Should().BeFalse("another learner's operation is not visible");
    }

    /// <summary>
    /// A malformed id is refused before anything runs.
    /// </summary>
    /// <remarks>
    /// The id becomes a primary key and appears in URLs, so the shapes it may take are bounded at
    /// the edge rather than left to the database or the router to reject unevenly.
    /// </remarks>
    [Theory]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("has?query")]
    public async Task A_malformed_operation_id_is_refused(string malformed)
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Chat();
        var result = await harness.TurnAsync(conversationId, "Hello", operationId: malformed);

        result.IsOk.Should().BeFalse();
        harness.Coach.RunCount.Should().Be(0);
    }

    private static CoachAgentTurnResult Chat() => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = "Good to hear from you."
        }
    };
}
