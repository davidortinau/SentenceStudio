using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The checkpoint is a replaceable 24-hour cache over a permanent conversation. These tests pin
/// the consequence of that: losing the checkpoint must never look to the learner like losing the
/// conversation.
/// </summary>
public class CoachCheckpointLifecycleTests
{
    [Fact]
    public async Task The_checkpoint_takes_the_conversations_own_id()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await harness.TurnAsync(conversationId, "Start talking");

        var session = await harness.App.Sessions.LoadAsync(CoachConversationHarness.OwnerUserId, conversationId);

        session.IsUsable.Should().BeTrue();
        session.Session!.Id.Should().Be(conversationId,
            "one id keeps the checkpoint and the ledger linkable without a second lookup");
    }

    [Fact]
    public async Task An_expired_checkpoint_rebuilds_and_the_conversation_keeps_its_history()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        // Past the 24-hour checkpoint life. The ledger has no such lifetime.
        harness.Time.Advance(TimeSpan.FromDays(2));

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Second answer.");
        var second = await harness.TurnAsync(conversationId, "Second question");

        second.IsOk.Should().BeTrue(second.Detail);

        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Select(m => m.Payload!.Text).Should().Equal(
            "First question", "First answer.", "Second question", "Second answer.");
    }

    [Fact]
    public async Task An_expired_checkpoint_rebuilds_the_turn_from_the_ledger()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Use the polite form.");
        await harness.TurnAsync(conversationId, "Which ending should I use?");

        harness.Time.Advance(TimeSpan.FromDays(2));

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Same as before.");
        await harness.TurnAsync(conversationId, "And again?");

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.PriorMessages.Should().NotBeEmpty("a rebuilt turn carries the conversation it lost");
        rebuilt.PriorMessages.Select(m => m.Text).Should().Contain("Which ending should I use?");
        rebuilt.PriorMessages.Select(m => m.Text).Should().Contain("Use the polite form.");
    }

    [Fact]
    public async Task A_live_checkpoint_does_not_replay_history_into_the_turn()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Noted.");
        await harness.TurnAsync(conversationId, "First");
        await harness.TurnAsync(conversationId, "Second");

        // The agent still holds its own memory, so re-sending history would duplicate it.
        harness.Coach.Requests[^1].PriorMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_config_version_change_rebuilds_instead_of_hiding_the_conversation()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Under the old config.");
        await harness.TurnAsync(conversationId, "Before the change");

        // A deploy changes the agent's configuration identity. Everything the learner is entitled
        // to read back lives in the ledger, so the checkpoint is the only thing that is discarded.
        harness.App.Options.CurrentValue.AgentConfigVersion = "next-deploy";

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Under the new config.");
        var after = await harness.TurnAsync(conversationId, "After the change");

        after.IsOk.Should().BeTrue(after.Detail);
        harness.Coach.Requests[^1].PriorMessages.Select(m => m.Text)
            .Should().Contain("Before the change");

        var messages = await harness.Service.GetMessagesAsync(conversationId, null, null);
        messages.Value!.Items.Should().HaveCount(4, "a config change loses no history");
    }

    [Fact]
    public async Task A_checkpoint_that_trails_the_ledger_is_rebuilt_rather_than_trusted()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Answered.");
        await harness.TurnAsync(conversationId, "Question one");

        // Stand in for a crash between the ledger append and the checkpoint stamp: the checkpoint
        // is intact but its memory stops before the newest turn.
        await harness.App.Service.StampCheckpointAsync(
            conversationId,
            harness.App.Service.CheckpointIdentity(conversationId, coveredSequence: 0));

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Answered again.");
        await harness.TurnAsync(conversationId, "Question two");

        harness.Coach.Requests[^1].PriorMessages.Should().NotBeEmpty(
            "a checkpoint that never saw the last turn cannot be trusted to answer the next one");
    }

    [Fact]
    public async Task A_rotated_checkpoint_rebuilds_the_turn_from_the_ledger()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply(
            "좋아하다 is the verb 'to like'. 좋다 is the adjective 'to be good'.");
        await harness.TurnAsync(conversationId, "What's the difference between 좋아하다 and 좋다?");

        (await harness.App.CheckpointAsync(conversationId)).Should().NotBeNull(
            "the fixture needs a live checkpoint before it can prove one was rotated away");

        // Exactly what memory rotation does. CoachMemoryCheckpointRotator calls this store method,
        // which nulls the serialized agent session and deliberately leaves the row, its constraints,
        // and its coverage stamp untouched — so the checkpoint still loads and still covers the
        // ledger while holding nothing to resume from.
        await harness.App.Sessions.ClearAgentCheckpointsAsync(CoachConversationHarness.OwnerUserId);

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Here is another one.");
        var followUp = await harness.TurnAsync(conversationId, "Can you give me another example?");

        followUp.IsOk.Should().BeTrue(followUp.Detail);

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.AgentSessionJson.Should().BeNull("the rotated checkpoint has no agent memory left");
        rebuilt.PriorMessages.Should().NotBeEmpty(
            "a rotated checkpoint must seed the turn from the ledger, not answer an anaphoric " +
            "follow-up with no idea what it refers to");

        rebuilt.PriorMessages.Select(m => m.Text).Should()
            .Contain("What's the difference between 좋아하다 and 좋다?")
            .And.Contain("좋아하다 is the verb 'to like'. 좋다 is the adjective 'to be good'.");
    }

    [Fact]
    public async Task A_rotated_checkpoint_rebuilds_in_learner_then_coach_order()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Second answer.");
        await harness.TurnAsync(conversationId, "Second question");

        await harness.App.Sessions.ClearAgentCheckpointsAsync(CoachConversationHarness.OwnerUserId);

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Third answer.");
        await harness.TurnAsync(conversationId, "Third question");

        var rebuilt = harness.Coach.Requests[^1].PriorMessages;

        // Roles and order are the whole point: a transcript the model reads out of order, or with
        // the learner's words attributed to the coach, is worse than no transcript at all.
        rebuilt.Select(m => (m.Role, m.Text)).Should().ContainInOrder(
            (CoachMessageRole.Learner, "First question"),
            (CoachMessageRole.Coach, "First answer."),
            (CoachMessageRole.Learner, "Second question"),
            (CoachMessageRole.Coach, "Second answer."));
    }

    [Fact]
    public async Task Both_arms_rebuild_a_rotated_checkpoint_from_the_same_ledger()
    {
        var transcripts = new Dictionary<CoachImplementation, IReadOnlyList<CoachPriorMessage>>();

        foreach (var arm in new[] { CoachImplementation.Baseline, CoachImplementation.Harness })
        {
            using var harness = new CoachConversationHarness();
            harness.Coach.Implementation = arm;

            var conversationId = await harness.CreateConversationAsync();

            harness.Coach.NextResult = CoachDurableTurnTests.Reply("The polite form is -요.");
            await harness.TurnAsync(conversationId, "Which ending should I use?");

            await harness.App.Sessions.ClearAgentCheckpointsAsync(CoachConversationHarness.OwnerUserId);

            harness.Coach.NextResult = CoachDurableTurnTests.Reply("Here is one more.");
            await harness.TurnAsync(conversationId, "Can you give me another example?");

            transcripts[arm] = harness.Coach.Requests[^1].PriorMessages;
        }

        // The arms differ in the agent pipeline and nothing else. Rebuilding from the canonical
        // visible ledger is application behaviour, so an arm that rebuilds differently would be a
        // bug in arm selection rather than a property of the pipeline.
        transcripts[CoachImplementation.Harness].Should().NotBeEmpty();
        transcripts[CoachImplementation.Harness].Select(m => (m.Role, m.Text)).Should()
            .Equal(transcripts[CoachImplementation.Baseline].Select(m => (m.Role, m.Text)));
    }

    /// <summary>
    /// The prompt and tool policy versions exist to catch a deploy that changed how the agent
    /// behaves without changing the configured version string. A field that is always null on both
    /// sides compares equal forever, so this asserts they carry a real value derived from the
    /// prompt and the tool allow-list.
    /// </summary>
    [Fact]
    public void Checkpoint_coverage_carries_a_real_prompt_and_tool_policy_version()
    {
        using var harness = new CoachConversationHarness();

        var identity = harness.App.Service.CheckpointIdentity("conversation-1", coveredSequence: 3);

        identity.PromptVersion.Should().NotBeNullOrWhiteSpace(
            "a prompt edit has to be detectable without anyone remembering to bump a version");
        identity.ToolPolicyVersion.Should().NotBeNullOrWhiteSpace();
        identity.CoveredSequence.Should().Be(3);
    }

    /// <summary>
    /// Every element of the policy identity has to be load-bearing. If one of them can differ while
    /// <see cref="CoachCheckpointCoverage.Matches"/> still returns true, that element is decoration
    /// and the checkpoint outlives the deploy it should not have survived.
    /// </summary>
    [Theory]
    [InlineData("prompt")]
    [InlineData("tools")]
    [InlineData("model")]
    [InlineData("config")]
    public void A_divergent_policy_version_makes_the_checkpoint_incompatible(string diverging)
    {
        using var harness = new CoachConversationHarness();
        var current = harness.App.Service.CheckpointIdentity("conversation-1", coveredSequence: 3);

        var stale = diverging switch
        {
            "prompt" => current with { PromptVersion = "from-the-old-prompt" },
            "tools" => current with { ToolPolicyVersion = "before-the-new-tool" },
            "model" => current with { ModelPolicyVersion = "a-different-provider" },
            _ => current with { AgentConfigVersion = "last-deploy" }
        };

        stale.Matches(current).Should().BeFalse(
            $"a change to the {diverging} policy has to force a rebuild from the ledger");
    }

    [Fact]
    public async Task Rebuilt_history_carries_conversation_only_never_server_decisions()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        // A notice is a server-authored artefact. Replayed as history it would read like a fresh
        // instruction from the system, which is exactly the confusion to avoid.
        await harness.Messages.AppendAsync(harness.Owner, new AppendCoachMessageRequest(
            conversationId,
            CoachMessageRole.Learner,
            CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.LearnerText,
                Text = "Learner words",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));

        var noticeAppend = await harness.Messages.AppendAsync(harness.Owner, new AppendCoachMessageRequest(
            conversationId,
            CoachMessageRole.Coach,
            CoachMessageKind.Notice,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.Notice,
                Text = "SERVER NOTICE: apply the plan",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime,
                Notice = new CoachStoredNotice { ReasonCode = CoachNoticeReasonCodes.Default, Text = "SERVER NOTICE: apply the plan" }
            }));
        noticeAppend.Status.Should().Be(CoachHistoryStatus.Success,
            "the notice must actually persist or the exclusion assertion below proves nothing");

        harness.Time.Advance(TimeSpan.FromDays(2));

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Fine.");
        await harness.TurnAsync(conversationId, "Continue");

        var prior = harness.Coach.Requests[^1].PriorMessages;
        prior.Select(m => m.Text).Should().Contain("Learner words");
        prior.Should().NotContain(m => m.Text.Contains("SERVER NOTICE"),
            "a past server decision must never re-enter as an instruction");
    }

    [Fact]
    public async Task Rebuilt_history_is_bounded_by_the_message_cap()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        for (var i = 0; i < 80; i++)
        {
            await harness.Messages.AppendAsync(harness.Owner, new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Learner,
                CoachMessageKind.Text,
                new CoachMessagePayload
                {
                    Kind = CoachMessagePayloadKind.LearnerText,
                    Text = $"Message {i}",
                    CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
                }));
        }

        harness.Time.Advance(TimeSpan.FromDays(2));

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Fine.");
        await harness.TurnAsync(conversationId, "Continue");

        var prior = harness.Coach.Requests[^1].PriorMessages;

        prior.Count.Should().BeLessThanOrEqualTo(50, "an unbounded rebuild would grow without limit");
        prior.Sum(m => m.Text.Length).Should().BeLessThanOrEqualTo(8_000);

        // The newest message that *precedes* this turn. The turn's own line is already in the
        // ledger by the time the rebuild reads it, and replaying it here would tell the model its
        // current question had already been asked — see CoachRebuiltPriorMessageBoundaryTests.
        prior[^1].Text.Should().Be("Message 79", "the newest turns are the ones kept");
    }

    [Fact]
    public async Task Rebuilt_history_never_crosses_into_another_learners_conversation()
    {
        using var harness = new CoachConversationHarness();
        var mine = await harness.CreateConversationAsync(idempotencyKey: "mine");

        harness.ActAs(CoachConversationHarness.OtherUserId);
        var theirs = await harness.CreateConversationAsync(idempotencyKey: "theirs");
        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Their answer.");
        await harness.TurnAsync(theirs, "Their secret question");

        harness.ActAs(CoachConversationHarness.OwnerUserId);
        harness.Time.Advance(TimeSpan.FromDays(2));
        harness.Coach.NextResult = CoachDurableTurnTests.Reply("My answer.");
        await harness.TurnAsync(mine, "My question");

        harness.Coach.Requests[^1].PriorMessages
            .Should().NotContain(m => m.Text.Contains("Their secret"));
    }

    [Fact]
    public async Task Deleting_the_checkpoint_leaves_the_conversation_and_its_ledger_intact()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Kept.");
        await harness.TurnAsync(conversationId, "Keep this");

        // Checkpoint expiry cleanup removes the session row and nothing else.
        await harness.App.Sessions.DeleteAsync(CoachConversationHarness.OwnerUserId, conversationId);

        var conversation = await harness.Service.GetAsync(conversationId);
        conversation.IsOk.Should().BeTrue();
        conversation.Value!.HasActiveCheckpoint.Should().BeFalse();

        var messages = await harness.Service.GetMessagesAsync(conversationId, null, null);
        messages.Value!.Items.Should().HaveCount(2, "the ledger outlives the checkpoint");
    }
}
