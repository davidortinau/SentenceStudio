using System.Text;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// What the model actually receives when a conversation is picked up again after the process that
/// started it is gone.
/// </summary>
/// <remarks>
/// <para>
/// A restarted coach has exactly two ways to remember: the serialized <c>AgentSession</c> the
/// previous turn stored, or — when that state is missing, unreadable, or was rotated away — the
/// visible message ledger, replayed into the turn as <c>PriorMessages</c>. Both arms must support
/// both routes, because the application chooses between them and does not know which arm is
/// selected.
/// </para>
/// <para>
/// These tests assert on the request transcript the chat client is handed, not on model output.
/// A live model would let a lucky guess pass for memory; recording the transcript proves the
/// context was there to be used.
/// </para>
/// </remarks>
public class CoachAgentSessionResumeTests
{
    private const string Answer = """{"Kind":"NoChange","CoachMessage":"ok"}""";

    private const string FirstQuestion = "What's the difference between 좋아하다 and 좋다?";
    private const string FirstAnswer = "좋아하다 is the verb 'to like'. 좋다 is the adjective 'to be good'.";
    private const string FollowUp = "Can you give me one last short example?";

    /// <summary>
    /// Records every request transcript and answers with the same canned intent. The reply carries
    /// the author name, message id, created-at stamp, finish reason, and usage a real provider
    /// returns, so the round trip is exercised over the shape that is actually serialized.
    /// </summary>
    private sealed class TranscriptRecordingChatClient : IChatClient
    {
        private int _calls;

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

        public IReadOnlyList<ChatMessage> LastCall => Calls[^1];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            var n = Interlocked.Increment(ref _calls);

            var reply = new ChatMessage(ChatRole.Assistant, Answer)
            {
                AuthorName = "learning-coach",
                MessageId = $"msg-{n}",
                CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(n)
            };

            return Task.FromResult(new ChatResponse(reply)
            {
                ResponseId = $"resp-{n}",
                ModelId = "test-model",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The coach never streams.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Every message of one call flattened, in order, for order-sensitive assertions.</summary>
    private static string Flatten(IReadOnlyList<ChatMessage> messages) =>
        string.Join("\n", messages.Select(m => $"[{m.Role}] {m.Text}"));

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task A_resumed_session_carries_the_earlier_turns_into_the_next_process(
        CoachImplementation implementation)
    {
        // The process that started the conversation.
        var before = new TranscriptRecordingChatClient();
        var coachBefore = CoachAgentTestDoubles.CreateCoach(implementation, before);

        var first = await coachBefore.RunTurnAsync(CoachAgentTestDoubles.NewRequest(FirstQuestion));
        first.Outcome.Should().Be(CoachAgentOutcome.Completed);
        first.AgentSessionJson.Should().NotBeNullOrWhiteSpace(
            "a completed turn is what gives the next one something to resume");

        // The process that picks it up: a new factory, a new agent, and a new chat client, so
        // nothing can be carried in memory. Only the serialized session crosses the boundary.
        var after = new TranscriptRecordingChatClient();
        var coachAfter = CoachAgentTestDoubles.CreateCoach(implementation, after);

        var resumed = await coachAfter.RunTurnAsync(
            CoachAgentTestDoubles.NewRequest(FollowUp, first.AgentSessionJson));

        resumed.Outcome.Should().Be(CoachAgentOutcome.Completed);

        var transcript = after.LastCall;

        transcript.Should().HaveCountGreaterThan(1,
            "a resumed turn is not a first turn: the earlier exchange has to be in the request");

        Flatten(transcript).Should().Match(
            $"*{FirstQuestion}*{Answer}*{FollowUp}*",
            "the learner's question, the coach's answer, and the follow-up must arrive in that order");

        transcript[0].Role.Should().Be(ChatRole.User);
        transcript.Should().Contain(m => m.Role == ChatRole.Assistant);
        transcript[^1].Role.Should().Be(ChatRole.User);
        transcript[^1].Text.Should().Contain(FollowUp);
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task A_rebuilt_turn_carries_the_ledger_when_there_is_no_session_to_resume(
        CoachImplementation implementation)
    {
        // The shape the application produces when the checkpoint is absent, unreadable, or was
        // rotated away: no agent session, and the visible ledger replayed as prior messages.
        var client = new TranscriptRecordingChatClient();
        var coach = CoachAgentTestDoubles.CreateCoach(implementation, client);

        var request = CoachAgentTestDoubles.NewRequest(FollowUp) with
        {
            AgentSessionJson = null,
            PriorMessages = new[]
            {
                new CoachPriorMessage(CoachMessageRole.Learner, FirstQuestion),
                new CoachPriorMessage(CoachMessageRole.Coach, FirstAnswer)
            }
        };

        var result = await coach.RunTurnAsync(request);

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);

        var sent = Flatten(client.LastCall);

        sent.Should().Match($"*learner: {FirstQuestion}*coach: {FirstAnswer}*{FollowUp}*",
            "a rebuilt turn replays the ledger role-tagged and in order, ahead of the new message");
    }

    /// <summary>
    /// The anaphora is the point. "Another example" only has a referent if the earlier topic
    /// reached the model, so the fake answers it from the request rather than from a script.
    /// </summary>
    [Theory]
    [InlineData(CoachImplementation.Baseline, true)]
    [InlineData(CoachImplementation.Harness, true)]
    [InlineData(CoachImplementation.Baseline, false)]
    [InlineData(CoachImplementation.Harness, false)]
    public async Task An_anaphoric_follow_up_resolves_only_when_the_earlier_topic_is_in_the_request(
        CoachImplementation implementation, bool rebuildFromLedger)
    {
        var client = new TopicAwareChatClient("좋아하다");
        var coach = CoachAgentTestDoubles.CreateCoach(implementation, client);

        CoachAgentTurnRequest request;
        if (rebuildFromLedger)
        {
            request = CoachAgentTestDoubles.NewRequest(FollowUp) with
            {
                PriorMessages = new[]
                {
                    new CoachPriorMessage(CoachMessageRole.Learner, FirstQuestion),
                    new CoachPriorMessage(CoachMessageRole.Coach, FirstAnswer)
                }
            };
        }
        else
        {
            var seed = CoachAgentTestDoubles.CreateCoach(implementation, new TopicAwareChatClient("좋아하다"));
            var first = await seed.RunTurnAsync(CoachAgentTestDoubles.NewRequest(FirstQuestion));
            request = CoachAgentTestDoubles.NewRequest(FollowUp, first.AgentSessionJson);
        }

        var result = await coach.RunTurnAsync(request);

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.CoachMessage.Should().Contain("좋아하다",
            "the follow-up can only be answered on topic when the earlier turns reached the model");
    }

    /// <summary>
    /// Answers on topic when the topic is somewhere in the request, and admits it cannot otherwise.
    /// Stands in for the judgement a live model makes, without the nondeterminism.
    /// </summary>
    private sealed class TopicAwareChatClient : IChatClient
    {
        private readonly string _topic;

        public TopicAwareChatClient(string topic) => _topic = topic;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sawTopic = messages.Any(m => m.Text.Contains(_topic, StringComparison.Ordinal));

            var json = sawTopic
                ? $$"""{"Kind":"NoChange","CoachMessage":"Another {{_topic}} example."}"""
                : """{"Kind":"AskClarification","CoachMessage":"Another example of what?"}""";

            var reply = new ChatMessage(ChatRole.Assistant, json)
            {
                AuthorName = "learning-coach",
                MessageId = Guid.NewGuid().ToString("N")
            };

            return Task.FromResult(new ChatResponse(reply) { FinishReason = ChatFinishReason.Stop });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // --- Phase 1 continuity repair: malformed AgentSession deserialization fallback ---

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task Malformed_agent_session_json_with_no_prior_messages_signals_RequiresRebuild(
        CoachImplementation implementation)
    {
        // A non-empty but unparseable AgentSessionJson triggers the deserialization fallback.
        // When no PriorMessages are provided, the turn must NOT call the model. Instead it
        // returns RequiresRebuild=true so the conversation layer can rebuild from the ledger.
        var client = new TranscriptRecordingChatClient();
        var coach = CoachAgentTestDoubles.CreateCoach(implementation, client);

        var request = CoachAgentTestDoubles.NewRequest("How do I say hello?") with
        {
            AgentSessionJson = "{ this is not valid json at all",
            PriorMessages = Array.Empty<CoachPriorMessage>()
        };

        var result = await coach.RunTurnAsync(request);

        result.RequiresRebuild.Should().BeTrue(
            "a malformed session with no prior messages must signal rebuild, not silently lose context");
        client.Calls.Should().BeEmpty(
            "no model call may occur before the rebuild — tokens must not be spent");
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task Malformed_agent_session_json_with_prior_messages_proceeds_without_RequiresRebuild(
        CoachImplementation implementation)
    {
        // When PriorMessages ARE available (the ledger was already rebuilt by the caller),
        // the turn proceeds with the rebuilt context and does not signal RequiresRebuild.
        var client = new TranscriptRecordingChatClient();
        var coach = CoachAgentTestDoubles.CreateCoach(implementation, client);

        var request = CoachAgentTestDoubles.NewRequest("How do I say hello?") with
        {
            AgentSessionJson = "{ this is not valid json at all",
            PriorMessages = new[]
            {
                new CoachPriorMessage(CoachMessageRole.Learner, "Previous question"),
                new CoachPriorMessage(CoachMessageRole.Coach, "Previous answer")
            }
        };

        var result = await coach.RunTurnAsync(request);

        result.RequiresRebuild.Should().BeFalse(
            "when prior messages are present, the turn proceeds with rebuilt context");
        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        client.Calls.Should().NotBeEmpty("the model should be called with the rebuilt context");
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task Phrase_reference_continuity_after_synthetic_schema_mismatch(
        CoachImplementation implementation)
    {
        // Simulate a schema-mismatched AgentSession: the json is well-formed JSON but not
        // a valid AgentSession (schema version change). The turn is retried with prior
        // messages containing the earlier exchange, and the follow-up resolves correctly.
        var client = new TopicAwareChatClient("좋아하다");
        var coach = CoachAgentTestDoubles.CreateCoach(implementation, client);

        // First: malformed session + prior messages = simulates a rebuilt retry.
        var request = CoachAgentTestDoubles.NewRequest(FollowUp) with
        {
            AgentSessionJson = """{"__incompatible_schema__": true, "version": 99}""",
            PriorMessages = new[]
            {
                new CoachPriorMessage(CoachMessageRole.Learner, FirstQuestion),
                new CoachPriorMessage(CoachMessageRole.Coach, FirstAnswer)
            }
        };

        var result = await coach.RunTurnAsync(request);

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.RequiresRebuild.Should().BeFalse(
            "prior messages were provided, so no rebuild signal is needed");
        result.Intent!.CoachMessage.Should().Contain("좋아하다",
            "the follow-up must resolve on topic from the rebuilt prior messages");
    }
}
