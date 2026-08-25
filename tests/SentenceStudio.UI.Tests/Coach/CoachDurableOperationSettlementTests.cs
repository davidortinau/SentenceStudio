using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using Xunit;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// What the client does with the state a submit reports, and why the server may never report
/// Running for a turn it has already answered.
/// </summary>
/// <remarks>
/// <para>
/// The observed failure was a coach turn that worked and looked like a hang. The server completed
/// the turn, lost a concurrency race with its own lease heartbeat, and answered OK with the copy
/// of the operation row it had read before the completion — which still said Running. The client
/// did exactly what it is supposed to do with a Running operation that carries messages: it merged
/// the answer and started polling. Nothing was ever going to move that row, so it polled until its
/// budget ran out and raised a timeout on a turn whose reply was already on screen.
/// </para>
/// <para>
/// These pin the client half of that contract, so the server-side fix has something to be a fix
/// <em>of</em>: a terminal answer settles with no polling at all, and a Running answer is a
/// promise that somebody is still working — the state the server must not use to describe a turn
/// that is over.
/// </para>
/// </remarks>
public sealed class CoachDurableOperationSettlementTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        return (new CoachWorkspaceState(client, new CoachConversationDirectory(client)), client);
    }

    [Fact]
    public async Task A_completed_submit_settles_without_polling_the_operation()
    {
        var (state, client) = Create();

        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var reply = client.Seed(conversationId, CoachMessageRole.Coach, "Sam replies.");

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = CoachStateMachineTests.Turn(),
                Messages = new[] { learner, reply },
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = reply.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = "How do I order coffee?";
        await state.SendDraftAsync();

        client.GetConversationOperationCalls.Should().Be(
            0,
            "a turn the server says is finished is finished; polling it is the four-minute wait this contract exists to avoid");

        state.HasRecoverableTurn.Should().BeFalse("nothing is outstanding");
        state.LastOperationState.Should().Be(CoachTurnOperationState.Completed);
    }

    [Fact]
    public async Task A_running_submit_is_taken_at_its_word_and_polled()
    {
        var (state, client) = Create();

        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            // The shape the server used to return for a turn it had actually completed: the
            // answer's rows, under a state that says the work is still going.
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var reply = client.Seed(conversationId, CoachMessageRole.Coach, "Sam replies.");

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Running,
                Messages = new[] { learner, reply },
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = reply.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        // Answered on the first poll, purely so the test does not have to sit out the client's
        // real budget. Against the server that reported Running for a finished turn there was no
        // such answer coming, and the wait ran to its deadline.
        client.OnGetConversationOperation = (conversationId, operationId) => new CoachTurnOperationDto
        {
            OperationId = operationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Completed,
            Result = CoachStateMachineTests.Turn(),
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = "How do I order coffee?";
        await state.SendDraftAsync();

        client.GetConversationOperationCalls.Should().BeGreaterThan(
            0,
            "Running means somebody is still working, so the client waits for them - which is exactly why the server must not say it about a turn that is over");
    }
}
