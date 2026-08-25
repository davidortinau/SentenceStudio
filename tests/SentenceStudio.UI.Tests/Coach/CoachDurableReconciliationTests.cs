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
/// Reconciliation between the optimistic copy of a turn and the canonical ledger rows.
/// </summary>
/// <remarks>
/// <para>
/// These cover a failure seen in the browser rather than in a test: after one durable turn the
/// transcript read Sam's canonical answer stamped 4:52 AM, a second copy of the same answer
/// stamped 11:52 PM, and the learner's own message underneath both. Three separate defects, all
/// visible in one screenshot:
/// </para>
/// <list type="number">
/// <item>the response body was applied on top of the canonical rows, under different message ids,
/// so nothing recognised it as the same answer;</item>
/// <item>the canonical learner row was never merged, so the optimistic copy kept standing in for a
/// row that already existed - and a local entry has no server sequence, so it sorted last;</item>
/// <item>the server's UTC stamp was read as though it were already local, relabelling 04:52Z as
/// 4:52 AM instead of the 11:52 PM the learner was actually at.</item>
/// </list>
/// </remarks>
public sealed class CoachDurableReconciliationTests
{
    private static (CoachWorkspaceState State, CoachConversationDirectory Directory, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var directory = new CoachConversationDirectory(client);
        return (new CoachWorkspaceState(client, directory), directory, client);
    }

    private static CoachWorkspaceState Restart(FakeCoachApiClient client)
    {
        return new CoachWorkspaceState(client, new CoachConversationDirectory(client));
    }

    /// <summary>Builds the exact operation shape the live server returned: Sam's row only.</summary>
    private static void ReplyCarryingOnlySamsRow(FakeCoachApiClient client, CoachAnswerDto? answer = null)
    {
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            // Both rows are written to the ledger, as the server does, but the operation carries
            // only the response half. That partial carry is what left the learner's optimistic
            // copy standing.
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var reply = client.Seed(conversationId, CoachMessageRole.Coach, "Sam replies.");

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = CoachStateMachineTests.Turn(answer: answer),
                Messages = new[] { reply },
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = reply.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };
    }

    private static async Task<CoachWorkspaceState> OneTurnAsync(FakeCoachApiClient client, CoachWorkspaceState state, string text)
    {
        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = text;
        await state.SendDraftAsync();
        return state;
    }

    // ------------------------------------------------------------------ the reported shape

    [Fact]
    public async Task ATurnLeavesExactlyOneLearnerMessageAndOneSamMessage()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await OneTurnAsync(client, state, "How do I say hello?");

        var messages = state.Timeline
            .Where(e => e.Kind is CoachTimelineKind.LearnerMessage or CoachTimelineKind.CoachMessage)
            .ToList();

        messages.Should().HaveCount(2, "the ledger holds one learner row and one Sam row");
        messages[0].Kind.Should().Be(CoachTimelineKind.LearnerMessage);
        messages[1].Kind.Should().Be(CoachTimelineKind.CoachMessage);
    }

    [Fact]
    public async Task SamsAnswerIsNotAppendedASecondTimeFromTheResponseBody()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await OneTurnAsync(client, state, "How do I say hello?");

        state.Timeline
            .Count(e => e.Kind == CoachTimelineKind.CoachMessage)
            .Should().Be(1, "the response describes the same answer the ledger already carries");
    }

    [Fact]
    public async Task TheLearnerMessageAdoptsTheCanonicalRowRatherThanStayingLocal()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await OneTurnAsync(client, state, "How do I say hello?");

        var learner = state.Timeline.Single(e => e.Kind == CoachTimelineKind.LearnerMessage);

        learner.ServerSequence.Should().NotBeNull("an unreconciled local entry sorts below Sam's reply");
        learner.Status.Should().Be(CoachTimelineStatus.Settled);
        learner.MessageId.Should().NotStartWith("local-");
    }

    [Fact]
    public async Task TheTranscriptReadsInServerSequenceOrder()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await OneTurnAsync(client, state, "How do I say hello?");

        var sequences = state.Timeline
            .Where(e => e.ServerSequence is not null)
            .Select(e => e.ServerSequence!.Value)
            .ToList();

        sequences.Should().Equal(sequences.OrderBy(s => s));
        state.Timeline.First(e => e.ServerSequence is not null).Kind
            .Should().Be(CoachTimelineKind.LearnerMessage, "sequence 1 is the learner's own message");
    }

    // ------------------------------------------------------------------ timestamps

    [Fact]
    public async Task ServerStampsRenderInTheLearnersOwnTimeZone()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await OneTurnAsync(client, state, "How do I say hello?");

        foreach (var entry in state.Timeline.Where(e => e.ServerSequence is not null))
        {
            var utc = entry.Message!.CreatedAtUtc;
            var expected = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime();

            // The bug read 04:52Z as 4:52 local. The instant must survive, and the offset must be
            // the learner's, so 04:52Z shows as 11:52 PM where the learner actually is.
            entry.Timestamp.UtcDateTime.Should().Be(expected.UtcDateTime);
            entry.Timestamp.Offset.Should().Be(expected.Offset);
        }
    }

    [Fact]
    public async Task ReconciledAndFreshlyMergedRowsAgreeOnTheClock()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await OneTurnAsync(client, state, "How do I say hello?");

        // The learner row arrived through reconciliation and Sam's through the merge. Two code
        // paths that disagree about the offset put two clocks in one transcript.
        var offsets = state.Timeline
            .Where(e => e.ServerSequence is not null)
            .Select(e => e.Timestamp.Offset)
            .Distinct()
            .ToList();

        offsets.Should().ContainSingle();
        offsets[0].Should().Be(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    // ------------------------------------------------------------------ across a restart

    [Fact]
    public async Task AReloadShowsTheSameTwoMessagesInTheSameOrder()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await OneTurnAsync(client, state, "How do I say hello?");
        var before = state.Timeline
            .Where(e => e.ServerSequence is not null)
            .Select(e => (e.Kind, e.MessageId, e.Timestamp))
            .ToList();

        var conversationId = state.ConversationId!;
        var reloaded = Restart(client);
        await reloaded.OpenConversationAsync(CoachPresentation.Overlay, conversationId);

        var after = reloaded.Timeline
            .Where(e => e.ServerSequence is not null)
            .Select(e => (e.Kind, e.MessageId, e.Timestamp))
            .ToList();

        // What the learner saw before the reload and after it must be the same transcript. A
        // duplicate that only exists in the live circuit is still a duplicate the learner read.
        after.Should().Equal(before);
    }

    // ------------------------------------------------------------------ roles

    [Fact]
    public async Task ALearnerRowNeverRendersAsSam()
    {
        var (state, _, client) = Create();
        var conversation = client.AddConversation("c-" + Guid.NewGuid().ToString("n")[..8]);
        client.Seed(conversation.ConversationId, CoachMessageRole.Learner, "My words.");
        client.Seed(conversation.ConversationId, CoachMessageRole.Coach, "Sam's words.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, conversation.ConversationId);

        // Reading the role by ordinal position, by numeric cast, or by "the first one is mine"
        // all pass by accident on a tidy two-row ledger and fail the moment a page starts on a
        // Sam row. The explicit role is the only thing that carries.
        var byText = state.Timeline
            .Where(e => e.Message is not null)
            .ToDictionary(e => e.Message!.Text ?? string.Empty, e => e.Kind);

        byText["My words."].Should().Be(CoachTimelineKind.LearnerMessage);
        byText["Sam's words."].Should().Be(CoachTimelineKind.CoachMessage);
    }

    [Fact]
    public async Task APageThatStartsOnASamRowStillLabelsEveryRowCorrectly()
    {
        var (state, _, client) = Create();
        var conversation = client.AddConversation("c-" + Guid.NewGuid().ToString("n")[..8]);
        client.Seed(conversation.ConversationId, CoachMessageRole.Coach, "Sam opens.");
        client.Seed(conversation.ConversationId, CoachMessageRole.Learner, "Learner answers.");
        client.Seed(conversation.ConversationId, CoachMessageRole.Coach, "Sam closes.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, conversation.ConversationId);

        state.Timeline
            .Where(e => e.Message is not null)
            .Select(e => e.Kind)
            .Should().Equal(
                CoachTimelineKind.CoachMessage,
                CoachTimelineKind.LearnerMessage,
                CoachTimelineKind.CoachMessage);
    }

    // ------------------------------------------------------------------ paging

    [Fact]
    public async Task EarlierMessagesLoadedAfterATurnStillSortAbioveIt()
    {
        var (state, _, client) = Create();
        var conversation = client.AddConversation("c-" + Guid.NewGuid().ToString("n")[..8]);

        for (var i = 0; i < 60; i++)
        {
            client.Seed(
                conversation.ConversationId,
                i % 2 == 0 ? CoachMessageRole.Learner : CoachMessageRole.Coach,
                "old " + i);
        }

        await state.OpenConversationAsync(CoachPresentation.Overlay, conversation.ConversationId);
        state.Draft = "and one more thing";
        await state.SendDraftAsync();

        await state.LoadEarlierMessagesAsync();

        var sequences = state.Timeline
            .Where(e => e.ServerSequence is not null)
            .Select(e => e.ServerSequence!.Value)
            .ToList();

        // Paging arrives out of order by construction: the oldest messages are fetched last.
        sequences.Should().Equal(sequences.OrderBy(s => s), "read order is the server sequence, not arrival");
        sequences.Should().OnlyHaveUniqueItems();
        sequences.Last().Should().Be(sequences.Max(), "the turn just sent is still the newest thing on screen");
    }

    [Fact]
    public async Task PagingDoesNotResurrectTheOptimisticCopyOfATurn()
    {
        var (state, _, client) = Create();
        var conversation = client.AddConversation("c-" + Guid.NewGuid().ToString("n")[..8]);

        for (var i = 0; i < 60; i++)
        {
            client.Seed(conversation.ConversationId, CoachMessageRole.Coach, "old " + i);
        }

        await state.OpenConversationAsync(CoachPresentation.Overlay, conversation.ConversationId);
        state.Draft = "unique learner text";
        await state.SendDraftAsync();
        await state.LoadEarlierMessagesAsync();

        state.Timeline
            .Count(e => e.Message?.Text == "unique learner text")
            .Should().Be(1);
    }

    // ------------------------------------------------------------------ overlapping turns

    [Fact]
    public async Task TwoTurnsInARowLeaveFourMessagesAndNoDuplicates()
    {
        var (state, _, client) = Create();
        ReplyCarryingOnlySamsRow(client);

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = "first question";
        await state.SendDraftAsync();
        state.Draft = "second question";
        await state.SendDraftAsync();

        var messages = state.Timeline
            .Where(e => e.Kind is CoachTimelineKind.LearnerMessage or CoachTimelineKind.CoachMessage)
            .ToList();

        messages.Should().HaveCount(4);
        messages.Select(e => e.MessageId).Should().OnlyHaveUniqueItems();
        messages.Select(e => e.Kind).Should().Equal(
            CoachTimelineKind.LearnerMessage,
            CoachTimelineKind.CoachMessage,
            CoachTimelineKind.LearnerMessage,
            CoachTimelineKind.CoachMessage);
    }

    [Fact]
    public async Task APendingTurnStaysBelowEverythingAlreadySettled()
    {
        var (state, _, client) = Create();
        var conversation = client.AddConversation("c-" + Guid.NewGuid().ToString("n")[..8]);
        client.Seed(conversation.ConversationId, CoachMessageRole.Learner, "settled question");
        client.Seed(conversation.ConversationId, CoachMessageRole.Coach, "settled answer");

        await state.OpenConversationAsync(CoachPresentation.Overlay, conversation.ConversationId);

        // Hold the operation open so the optimistic copy is the only thing representing this turn.
        client.OnSubmitConversationTurn = (conversationId, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Running,
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        state.Draft = "brand new question";
        var run = state.SendDraftAsync();

        var pending = state.Timeline.LastOrDefault(e => e.Kind == CoachTimelineKind.LearnerMessage);
        pending!.Message!.Text.Should().Be("brand new question");
        pending.ServerSequence.Should().BeNull("nothing canonical exists for it yet");

        await state.CancelRunAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ structured answer

    [Fact]
    public async Task TheStructuredAnswerLandsOnTheCanonicalMessageNotADuplicate()
    {
        var (state, _, client) = Create();
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        ReplyCarryingOnlySamsRow(client, answer);

        await OneTurnAsync(client, state, "How do I say hello?");

        var coachEntries = state.Timeline.Where(e => e.Kind == CoachTimelineKind.CoachMessage).ToList();

        // The ledger row is the one on screen and it did not bring the structured answer, so
        // dropping the response body wholesale would silently downgrade every answer to plain
        // text. It has to be carried across onto the canonical message.
        coachEntries.Should().ContainSingle();
        coachEntries[0].ServerSequence.Should().NotBeNull();
        coachEntries[0].Answer.Should().BeSameAs(answer);
        state.LatestAnswer.Should().BeSameAs(answer);
    }
}
