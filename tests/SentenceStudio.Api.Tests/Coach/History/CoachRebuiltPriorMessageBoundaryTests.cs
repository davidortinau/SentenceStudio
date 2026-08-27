using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// A rebuilt turn replays the conversation that came <em>before</em> the message being answered.
/// These tests pin that boundary.
/// </summary>
/// <remarks>
/// <para>
/// The learner's message is appended to the ledger before the checkpoint is consulted, because a
/// model call that never returns must not also swallow what the learner typed. That ordering makes
/// the rebuild read a ledger that already contains the very message the turn is about, so an
/// unbounded read hands the model the same sentence twice: once inside
/// <c>EARLIER IN THIS CONVERSATION</c>, where it reads as something already said and answered, and
/// again inside <c>LEARNER MESSAGE</c>, where it is the thing to answer.
/// </para>
/// <para>
/// Existing coverage asserts containment and ordering, both of which a duplicate satisfies. These
/// assert the exact transcript and the exact number of occurrences, which a duplicate does not.
/// </para>
/// </remarks>
public class CoachRebuiltPriorMessageBoundaryTests
{
    [Fact]
    public async Task A_rotated_rebuild_replays_the_conversation_without_the_message_it_is_answering()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("좋아하다 is a verb.");
        await harness.TurnAsync(conversationId, "What's the difference?");

        // Memory rotation: the row survives and still covers the ledger, but there is nothing to
        // resume from, so the next turn rebuilds.
        await harness.App.Sessions.ClearAgentCheckpointsAsync(CoachConversationHarness.OwnerUserId);

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Here is one.");
        await harness.TurnAsync(conversationId, "Can you give me another example?");

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.PriorMessages.Select(m => (m.Role, m.Text)).Should().Equal(
            (CoachMessageRole.Learner, "What's the difference?"),
            (CoachMessageRole.Coach, "좋아하다 is a verb."));

        ShouldAppearExactlyOnce(rebuilt, "Can you give me another example?");
    }

    [Fact]
    public async Task An_expired_rebuild_replays_the_conversation_without_the_message_it_is_answering()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        harness.Time.Advance(TimeSpan.FromDays(2));

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Second answer.");
        await harness.TurnAsync(conversationId, "Second question");

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.PriorMessages.Select(m => (m.Role, m.Text)).Should().Equal(
            (CoachMessageRole.Learner, "First question"),
            (CoachMessageRole.Coach, "First answer."));

        ShouldAppearExactlyOnce(rebuilt, "Second question");
    }

    [Fact]
    public async Task A_config_change_rebuild_replays_the_conversation_without_the_message_it_is_answering()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Under the old config.");
        await harness.TurnAsync(conversationId, "Before the change");

        harness.App.Options.CurrentValue.AgentConfigVersion = "next-deploy";

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Under the new config.");
        await harness.TurnAsync(conversationId, "After the change");

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.PriorMessages.Select(m => (m.Role, m.Text)).Should().Equal(
            (CoachMessageRole.Learner, "Before the change"),
            (CoachMessageRole.Coach, "Under the old config."));

        ShouldAppearExactlyOnce(rebuilt, "After the change");
    }

    /// <summary>
    /// Key rotation or tampering leaves the checkpoint's agent state undecryptable. The row loads,
    /// the load reports <see cref="Api.Coach.Persistence.CoachSessionLoadStatus.Unreadable"/>, and
    /// the turn rebuilds from the ledger — which is the same boundary, reached a different way.
    /// </summary>
    [Fact]
    public async Task An_unreadable_checkpoint_rebuild_replays_the_conversation_without_the_message_it_is_answering()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Answered once.");
        await harness.TurnAsync(conversationId, "Asked once");

        await CorruptAgentSessionAsync(harness, conversationId);

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Answered twice.");
        var second = await harness.TurnAsync(conversationId, "Asked twice");

        second.IsOk.Should().BeTrue(second.Detail);

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.PriorMessages.Select(m => (m.Role, m.Text)).Should().Equal(
            (CoachMessageRole.Learner, "Asked once"),
            (CoachMessageRole.Coach, "Answered once."));

        ShouldAppearExactlyOnce(rebuilt, "Asked twice");
    }

    /// <summary>
    /// The retry is the case a boundary drawn from the ledger head gets wrong. The dead attempt
    /// already wrote the learner row, so the head a recovering attempt reads <em>includes</em> the
    /// message being answered: bounding on it replays that message, and over-correcting drops the
    /// turn before it. Both halves are asserted — the exact history that survives, and the single
    /// occurrence of the message being answered.
    /// </summary>
    [Fact]
    public async Task A_retried_turn_keeps_its_earlier_turns_and_still_does_not_replay_its_own_message()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("First answer.");
        await harness.TurnAsync(conversationId, "First question");

        // Operations are ordered by creation time, and the harness clock does not tick on its own.
        // Without this, the completed first operation and the crashed second one share a timestamp
        // and the process-death helper cannot tell which one it is meant to rewind.
        harness.Time.Advance(TimeSpan.FromMinutes(1));

        // Die after the learner row is durable and before the model answers.
        harness.Coach.OnRun = _ => throw new InvalidOperationException("Simulated crash mid-turn.");

        var key = Guid.NewGuid().ToString("N");
        var crash = () => harness.TurnAsync(conversationId, "Second question", key);
        await crash.Should().ThrowAsync<InvalidOperationException>();

        await harness.SimulateProcessDeathAsync(conversationId);

        harness.Coach.OnRun = null;
        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Second answer.");
        harness.Restart();
        harness.ActAs(CoachConversationHarness.OwnerUserId);

        // The retry rebuilds whether or not the checkpoint survived: the dead attempt already
        // advanced the ledger past the coverage the checkpoint was stamped with, so the checkpoint
        // no longer covers the conversation and is replaced.
        var retried = await harness.TurnAsync(conversationId, "Second question", key);
        retried.IsOk.Should().BeTrue(retried.Detail);

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.PriorMessages.Select(m => (m.Role, m.Text)).Should().Equal(
            (CoachMessageRole.Learner, "First question"),
            (CoachMessageRole.Coach, "First answer."));

        ShouldAppearExactlyOnce(rebuilt, "Second question");

        // The retry re-used the durable learner row rather than writing a second one.
        var ledger = await harness.LedgerAsync(conversationId);
        ledger.Select(m => m.Payload!.Text).Should().Equal(
            "First question", "First answer.", "Second question", "Second answer.");
    }

    /// <summary>
    /// A chip tap writes no learner line, so there is no row to stop before. The bound falls back
    /// to one past the ledger head the turn measured, which must include the newest coach reply
    /// rather than stopping one short of it.
    /// </summary>
    [Fact]
    public async Task A_turn_with_no_learner_prose_replays_the_whole_conversation()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("The polite form is -요.");
        await harness.TurnAsync(conversationId, "Which ending should I use?");

        await harness.App.Sessions.ClearAgentCheckpointsAsync(CoachConversationHarness.OwnerUserId);

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Here is one more.");
        var chip = await harness.Service.SubmitTurnAsync(conversationId, new CoachConversationTurnRequest
        {
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            OperationId = Guid.NewGuid().ToString("N"),
            Turn = new CoachTurnRequest
            {
                InputKind = CoachTurnInputKind.Chip,
                ChipId = "another-example"
            }
        });

        chip.IsOk.Should().BeTrue(chip.Detail);

        harness.Coach.Requests[^1].PriorMessages.Select(m => (m.Role, m.Text)).Should().Equal(
            (CoachMessageRole.Learner, "Which ending should I use?"),
            (CoachMessageRole.Coach, "The polite form is -요."));
    }

    /// <summary>
    /// The character budget is spent newest-first, so the message being answered used to take its
    /// share before any history was considered — and the oldest turn fell off the end to pay for a
    /// copy of something the model was about to be shown anyway.
    /// </summary>
    /// <remarks>
    /// A learner turn is capped at 500 characters by product validation, so the current message can
    /// never exhaust the 8,000-character budget on its own. It can, and did, push the budget over
    /// the edge when the conversation was already close to it — which is the ordinary case for a
    /// long-running conversation, not an adversarial one.
    /// </remarks>
    [Fact]
    public async Task The_current_message_no_longer_spends_the_history_budget()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        // Eight one-thousand-character messages fill the rebuild budget exactly.
        for (var i = 0; i < 8; i++)
        {
            await harness.Messages.AppendAsync(harness.Owner, new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Learner,
                CoachMessageKind.Text,
                new CoachMessagePayload
                {
                    Kind = CoachMessagePayloadKind.LearnerText,
                    Text = $"Message {i} " + new string('a', 1_000 - $"Message {i} ".Length),
                    CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
                }));
        }

        harness.Time.Advance(TimeSpan.FromDays(2));

        var current = "Continue " + new string('b', 400);

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Fine.");
        await harness.TurnAsync(conversationId, current);

        var rebuilt = harness.Coach.Requests[^1];

        rebuilt.PriorMessages.Should().HaveCount(8);
        rebuilt.PriorMessages[0].Text.Should().StartWith(
            "Message 0 ",
            "the oldest turn is only dropped when real history needs the room");
        rebuilt.PriorMessages.Sum(m => m.Text.Length).Should().BeLessThanOrEqualTo(8_000);

        ShouldAppearExactlyOnce(rebuilt, current);
    }

    [Fact]
    public async Task Both_arms_rebuild_to_the_same_boundary()
    {
        var transcripts = new Dictionary<CoachImplementation, IReadOnlyList<(CoachMessageRole Role, string Text)>>();

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

            var rebuilt = harness.Coach.Requests[^1];
            ShouldAppearExactlyOnce(rebuilt, "Can you give me another example?");

            transcripts[arm] = rebuilt.PriorMessages.Select(m => (m.Role, m.Text)).ToList();
        }

        transcripts[CoachImplementation.Baseline].Should().Equal(
            (CoachMessageRole.Learner, "Which ending should I use?"),
            (CoachMessageRole.Coach, "The polite form is -요."));

        transcripts[CoachImplementation.Harness].Should().Equal(transcripts[CoachImplementation.Baseline]);
    }

    /// <summary>
    /// The cap keeps the newest turns that actually precede this one. The old assertion — that the
    /// last replayed line is the current message — was the duplicate, restated as an expectation.
    /// </summary>
    [Fact]
    public async Task The_bounded_rebuild_keeps_the_newest_prior_turns_not_the_current_one()
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

        var rebuilt = harness.Coach.Requests[^1];
        var prior = rebuilt.PriorMessages;

        prior.Should().HaveCount(50, "the cap keeps the newest fifty messages that precede this turn");
        prior[0].Text.Should().Be("Message 30");
        prior[^1].Text.Should().Be("Message 79", "the newest message before this turn is the last line of history");

        ShouldAppearExactlyOnce(rebuilt, "Continue");
    }

    /// <summary>
    /// The message the turn is answering must appear once in the message the model actually
    /// receives, in the block that asks for an answer — not also in the block that describes what
    /// has already been said.
    /// </summary>
    private static void ShouldAppearExactlyOnce(CoachAgentTurnRequest request, string learnerText)
    {
        request.LearnerText.Should().Be(learnerText);

        request.PriorMessages.Should().NotContain(
            m => string.Equals(m.Text, learnerText, StringComparison.Ordinal),
            "the message being answered is not something the conversation already said");

        var prompt = CoachInstructions.BuildTurnMessage(request);

        Occurrences(prompt, learnerText).Should().Be(
            1,
            "the learner's message belongs in LEARNER MESSAGE only; a second copy under EARLIER IN " +
            "THIS CONVERSATION tells the model its current question was already asked and answered");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;

        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    /// <summary>Makes the stored agent state undecryptable, exactly as a lost key would.</summary>
    private static async Task CorruptAgentSessionAsync(CoachConversationHarness harness, string conversationId)
    {
        var row = await harness.Db.CoachSessions.FirstAsync(
            s => s.UserProfileId == CoachConversationHarness.OwnerUserId && s.Id == conversationId);

        row.ProtectedAgentSession.Should().NotBeNullOrEmpty(
            "the fixture needs stored agent state before it can prove one became unreadable");

        row.ProtectedAgentSession = "not-a-protected-payload";
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }
}
