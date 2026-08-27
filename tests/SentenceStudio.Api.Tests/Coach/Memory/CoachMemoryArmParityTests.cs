using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Both coach arms must handle memory identically: same delivery, same trust level, same parse.
/// </summary>
/// <remarks>
/// <para>
/// The baseline and harness arms assemble a turn differently, and memory is the newest thing they
/// assemble. If only one arm labels the block untrusted, or only one arm puts it in a user message
/// rather than a system message, then switching arms silently changes what a stored preference is
/// allowed to do — and that difference would only surface in production, on whichever arm was not
/// the one the tests happened to exercise.
/// </para>
/// <para>
/// These tests run the real arms against a recording chat client, so the assertions are about the
/// messages the provider is actually handed.
/// </para>
/// </remarks>
public class CoachMemoryArmParityTests
{
    private const string MemoryBlock =
        """
        UNTRUSTED SAVED LEARNING PREFERENCES
        - Study goal: prepare for a work trip to Seoul
        """;

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task BothArmsDeliverTheMemoryBlockToTheModel(CoachImplementation arm)
    {
        var client = new RecordingChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");

        await RunAsync(client, arm, MemoryBlock);

        var all = string.Join("\n", client.LastMessages!.Select(m => m.Text));
        all.Should().Contain("prepare for a work trip to Seoul",
            "{0} must actually pass selected memory to the model", arm);
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task BothArmsCarryTheMemoryBlockAsUntrustedDataNotAsInstructions(CoachImplementation arm)
    {
        var client = new RecordingChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");

        await RunAsync(client, arm, MemoryBlock);

        // The block is data the learner authored. Delivered as system or developer text it would
        // read as policy, and a stored preference would outrank the coach's own instructions.
        foreach (var message in client.LastMessages!.Where(m =>
                     m.Role == ChatRole.System || m.Role == ChatRole.Tool))
        {
            message.Text.Should().NotContain("prepare for a work trip to Seoul",
                "{0} must not deliver memory as {1} content", arm, message.Role);
        }

        var carrier = client.LastMessages!.Single(m => m.Text.Contains("prepare for a work trip to Seoul"));
        carrier.Role.Should().Be(ChatRole.User, "memory travels with the learner's turn, as data");
        carrier.Text.Should().Contain("UNTRUSTED", "the label must survive assembly on {0}", arm);
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task BothArmsOmitTheBlockEntirelyWhenNothingIsSelected(CoachImplementation arm)
    {
        var client = new RecordingChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");

        await RunAsync(client, arm, memoryBlock: null);

        var all = string.Join("\n", client.LastMessages!.Select(m => m.Text));

        // No empty heading, no "(none)" placeholder. An empty labelled section teaches the model
        // that the section exists and can be filled, which is a nudge toward inventing content.
        all.Should().NotContain("UNTRUSTED", "{0} must omit the block when there is no memory", arm);
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task BothArmsParseTheSameMemoryProposal(CoachImplementation arm)
    {
        var client = new RecordingChatClient(
            """
            {
              "Kind": "NoChange",
              "CoachMessage": "Saved that for later.",
              "MemoryProposal": {
                "Kind": "PersistentStudyGoal",
                "Scope": "TargetLanguage",
                "StudyGoalText": "prepare for a work trip to Seoul",
                "EvidenceSpan": "remember I am preparing for a work trip to Seoul"
              }
            }
            """);

        var result = await RunAsync(client, arm, MemoryBlock);

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.MemoryProposal.Should().NotBeNull("{0} must surface the proposal", arm);
        result.Intent.MemoryProposal!.Kind.Should().Be(CoachProposedMemoryKind.PersistentStudyGoal);
        result.Intent.MemoryProposal.StudyGoalText.Should().Be("prepare for a work trip to Seoul");
        result.Intent.MemoryProposal.EvidenceSpan
            .Should().Be("remember I am preparing for a work trip to Seoul");
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task BothArmsDescribeTheProposalInTheSameClosedSchema(CoachImplementation arm)
    {
        var client = new RecordingChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");

        await RunAsync(client, arm, MemoryBlock);

        var schema = ((ChatResponseFormatJson)client.LastOptions!.ResponseFormat!).Schema!.Value.GetRawText();

        // The wire format is camelCase, so the assertions match what the provider actually reads.
        schema.Should().Contain("memoryProposal", "{0} must let the model propose", arm);
        schema.Should().Contain("evidenceSpan", "the span is what the server revalidates against");

        // The proposal is closed: four kinds and two scopes, no free-form escape hatch.
        schema.Should().Contain("PersistentStudyGoal").And.Contain("ExampleRegister");
        schema.Should().Contain("TargetLanguage").And.Contain("Global");

        // No identifier, no version, no status. A schema that let the model emit any of them
        // would let it approve its own proposal, and approval is the learner's decision alone.
        schema.ToLowerInvariant().Should().NotContain("factid");
        schema.ToLowerInvariant().Should().NotContain("expectedversion");
        schema.ToLowerInvariant().Should().NotContain("\"status\"");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<CoachAgentTurnResult> RunAsync(
        IChatClient client,
        CoachImplementation arm,
        string? memoryBlock)
    {
        var services = new ServiceCollection();
        services.AddSingleton(client);

        using var provider = services.BuildServiceProvider();
        var options = new TestOptionsMonitor<CoachOptions>(new CoachOptions { Enabled = true });
        using var telemetry = new CoachTelemetry();
        var factory = new CoachAgentFactory(provider, options, NullLoggerFactory.Instance);

        ILearningCoach coach = arm == CoachImplementation.Baseline
            ? new BaselineLearningCoach(factory, new StubToolFactory(), options, telemetry,
                NullLogger<BaselineLearningCoach>.Instance)
            : new HarnessLearningCoach(factory, new StubToolFactory(), options, telemetry,
                NullLogger<HarnessLearningCoach>.Instance);

        return await coach.RunTurnAsync(new CoachAgentTurnRequest
        {
            SessionId = "session-1",
            LearnerText = "remember I am preparing for a work trip to Seoul",
            MemoryBlock = memoryBlock,
            ActiveConstraints = new CoachConstraintSetDto
            {
                AvailableMinutes = 10,
                AudioAllowed = true,
                SpeechAllowed = true,
                TypingAllowed = true,
                EnergyLevel = CoachEnergyLevel.Normal
            },
            ClarificationsRemaining = 2,
            UserLocalDate = new DateOnly(2026, 8, 17)
        });
    }

    /// <summary>A chat client that keeps the messages it was handed, so a test can read them.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string _json;

        public RecordingChatClient(string json) => _json = json;

        public ChatOptions? LastOptions { get; private set; }

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOptions = options;
            LastMessages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The coach never streams in version 1.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubToolFactory : ICoachToolFactory
    {
        public IReadOnlyList<AIFunction> CreateTools() =>
            CoachToolNames.All
                .Select(name => AIFunctionFactory.Create(
                    () => "stub", new AIFunctionFactoryOptions { Name = name, Description = $"Reads {name}." }))
                .ToList();
    }
}
