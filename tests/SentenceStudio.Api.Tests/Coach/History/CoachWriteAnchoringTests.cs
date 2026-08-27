using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// Where a proposal card lands in a conversation.
/// </summary>
/// <remarks>
/// A card in the wrong place is not a crash and not a failed request. It is a decision presented
/// next to the wrong sentence — the thing that turns "Sam offered to add 사과, shall I?" into a
/// button under an unrelated grammar answer. That failure is invisible to every other kind of
/// test, so the rule is pinned here on its own.
/// </remarks>
public class CoachWriteAnchoringTests
{
    private static CoachMessageRecord Message(
        string id, long sequence, CoachMessageRole role, string? turnOperationId) =>
        new(
            id,
            "conv-1",
            sequence,
            role,
            CoachMessageKind.Text,
            new CoachMessagePayload { Text = "hello" },
            SchemaVersion: 1,
            turnOperationId,
            new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc));

    private static CoachWriteOperationDto Write(string operationId, string? turnId) => new()
    {
        OperationId = operationId,
        ConversationId = "conv-1",
        TurnId = turnId,
        ChangeKind = CoachWriteChangeKind.VocabularyAdd,
        RiskClass = CoachWriteRiskClass.WriteSoft,
        Status = CoachWriteStatus.Proposed,
        ApprovalMode = "accept",
        Summary = "Add a word",
        ExpiresAtUtc = new DateTime(2026, 8, 19, 13, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void A_proposal_anchors_to_what_the_coach_said_not_to_what_the_learner_asked()
    {
        var records = new[]
        {
            Message("m1", 1, CoachMessageRole.Learner, "turn-1"),
            Message("m2", 2, CoachMessageRole.Coach, "turn-1")
        };

        var paired = CoachWriteAnchoring.ByMessage(records, [Write("op-1", "turn-1")]);

        paired.Should().ContainKey(1).WhoseValue.OperationId.Should().Be("op-1");
        paired.Should().NotContainKey(0, "the card belongs after the answer, not beside the question");
    }

    [Fact]
    public void A_proposal_anchors_to_the_last_thing_the_coach_said_in_its_turn()
    {
        var records = new[]
        {
            Message("m1", 1, CoachMessageRole.Learner, "turn-1"),
            Message("m2", 2, CoachMessageRole.Coach, "turn-1"),
            Message("m3", 3, CoachMessageRole.Coach, "turn-1")
        };

        var paired = CoachWriteAnchoring.ByMessage(records, [Write("op-1", "turn-1")]);

        paired.Keys.Should().Equal(2);
    }

    [Fact]
    public void Each_turn_keeps_its_own_proposal()
    {
        var records = new[]
        {
            Message("m1", 1, CoachMessageRole.Coach, "turn-1"),
            Message("m2", 2, CoachMessageRole.Coach, "turn-2")
        };

        var paired = CoachWriteAnchoring.ByMessage(
            records, [Write("op-1", "turn-1"), Write("op-2", "turn-2")]);

        paired[0].OperationId.Should().Be("op-1");
        paired[1].OperationId.Should().Be("op-2");
    }

    /// <summary>
    /// A turn that proposed twice shows the newest, which is the one the learner was last told
    /// about. Two live cards in one exchange is an invitation to approve the wrong one.
    /// </summary>
    [Fact]
    public void A_turn_that_proposed_twice_shows_the_newest()
    {
        var records = new[] { Message("m1", 1, CoachMessageRole.Coach, "turn-1") };

        // Creation order, as the ledger returns it.
        var paired = CoachWriteAnchoring.ByMessage(
            records, [Write("op-old", "turn-1"), Write("op-new", "turn-1")]);

        paired.Should().ContainSingle();
        paired[0].OperationId.Should().Be("op-new");
    }

    [Fact]
    public void A_proposal_whose_turn_is_not_on_this_page_is_left_out()
    {
        var records = new[] { Message("m1", 1, CoachMessageRole.Coach, "turn-1") };

        CoachWriteAnchoring.ByMessage(records, [Write("op-1", "turn-99")]).Should().BeEmpty(
            "a card with no context to sit in is worse than no card");
    }

    [Fact]
    public void A_proposal_with_no_turn_anchors_to_nothing()
    {
        var records = new[] { Message("m1", 1, CoachMessageRole.Coach, "turn-1") };

        CoachWriteAnchoring.ByMessage(records, [Write("op-1", null)]).Should().BeEmpty();
    }

    [Fact]
    public void A_page_with_no_messages_or_no_proposals_pairs_nothing()
    {
        CoachWriteAnchoring.ByMessage([], [Write("op-1", "turn-1")]).Should().BeEmpty();
        CoachWriteAnchoring.ByMessage(
            [Message("m1", 1, CoachMessageRole.Coach, "turn-1")], []).Should().BeEmpty();
    }

    /// <summary>
    /// The anchor is stamped onto the copy the client reads, so a reload can scroll back to the
    /// same card.
    /// </summary>
    [Fact]
    public void Anchoring_stamps_the_message_and_changes_nothing_else()
    {
        var write = Write("op-1", "turn-1");

        var anchored = CoachWriteAnchoring.Anchored(write, "m2");

        anchored.MessageId.Should().Be("m2");
        anchored.Should().BeEquivalentTo(write, options => options.Excluding(w => w.MessageId));
    }
}
