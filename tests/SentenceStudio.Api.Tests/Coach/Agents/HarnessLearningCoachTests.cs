using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// The harness arm, driven by a scripted chat client so the restricted pipeline is exercised
/// end to end without a network call.
/// </summary>
public class HarnessLearningCoachTests
{
    private const string DirectChangeJson = """
        {
          "Kind": "DirectConstraintChange",
          "ConstraintDelta": { "AvailableMinutes": 10, "AudioAllowed": false },
          "AcceptanceState": "NotApplicable",
          "CoachMessage": "Today's Plan now fits 10 minutes and uses no audio.",
          "EvidenceReferences": []
        }
        """;

    private static ILearningCoach NewCoach(IChatClient? chatClient, CoachOptions? options = null) =>
        CoachAgentTestDoubles.CreateCoach(CoachImplementation.Harness, chatClient, options);

    [Fact]
    public void TheArmReportsItself()
    {
        NewCoach(new ScriptedChatClient("{}")).Implementation.Should().Be(CoachImplementation.Harness);
    }

    [Fact]
    public async Task WithNoChatClient_ReportsModelUnavailableAndNeverBuildsAnAgent()
    {
        var probe = CoachAgentTestDoubles.CountingFactory(chatClient: null);
        var coach = CoachAgentTestDoubles.CreateCoach(
            CoachImplementation.Harness, chatClient: null, agentFactoryProbe: probe);

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("make it 10 minutes"));

        result.Outcome.Should().Be(CoachAgentOutcome.ModelUnavailable);
        result.Intent.Should().BeNull();
        probe.TotalAgentsBuilt.Should().Be(0);
    }

    [Fact]
    public async Task StructuredOutput_ProducesATypedTurnIntent()
    {
        var client = new ScriptedChatClient(DirectChangeJson);

        var result = await NewCoach(client).RunTurnAsync(CoachAgentTestDoubles.NewRequest("10 minutes, no audio"));

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.Kind.Should().Be(CoachIntentKind.DirectConstraintChange);
        result.Intent.ConstraintDelta!.AvailableMinutes.Should().Be(10);
        result.Intent.ConstraintDelta.AudioAllowed.Should().BeFalse();
        client.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task TheSchemaComesFromTheContractNotThePrompt()
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");

        await NewCoach(client).RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        client.LastOptions!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();

        var schema = ((ChatResponseFormatJson)client.LastOptions.ResponseFormat!).Schema!.Value.GetRawText();
        schema.Should().Contain("availableMinutes");
        schema.Should().Contain("The range is 3 to 90", "the [Description] text drives the schema");
    }

    [Fact]
    public async Task AllFiveReadOnlyToolsReachTheModelAndNothingElse()
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");

        await NewCoach(client).RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        var toolNames = client.LastOptions!.Tools!.Select(t => t.Name).ToArray();
        toolNames.Should().BeEquivalentTo(CoachToolNames.All,
            "the harness must not add a file, todo, skills, or web-search tool");
    }

    [Fact]
    public async Task NoWriteToolReachesTheModel()
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");

        await NewCoach(client).RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        foreach (var tool in client.LastOptions!.Tools!)
        {
            tool.Name.Should().NotContainAny("write", "update", "delete", "apply", "save", "create", "shell", "bash");
        }
    }

    [Fact]
    public async Task TheOutputTokenCapReachesTheModelRequest()
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");
        var coach = NewCoach(client, new CoachOptions { Enabled = true, MaxOutputTokens = 800 });

        await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        client.LastOptions!.MaxOutputTokens.Should().Be(800);
    }

    [Fact]
    public async Task ANonConformingAnswer_IsReportedAsInvalidOutputAndCarriesNoIntent()
    {
        var result = await NewCoach(new ScriptedChatClient("this is not json"))
            .RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.InvalidOutput);
        result.Intent.Should().BeNull();
    }

    [Fact]
    public async Task ACancelledRun_IsReportedAsCancelled()
    {
        var coach = NewCoach(new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}"""));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"), cts.Token);

        result.Outcome.Should().Be(CoachAgentOutcome.Cancelled);
    }

    [Fact]
    public async Task ARunThatOutlastsTheBudget_IsReportedAsTimeout()
    {
        var coach = NewCoach(
            new SlowChatClient(TimeSpan.FromSeconds(5), """{"Kind":"NoChange","CoachMessage":"ok"}"""),
            new CoachOptions { Enabled = true, RequestTimeoutSeconds = 1 });

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.Timeout);
        result.Intent.Should().BeNull();
    }

    [Fact]
    public async Task TheRunReturnsResumableSessionState()
    {
        var result = await NewCoach(new ScriptedChatClient(DirectChangeJson))
            .RunTurnAsync(CoachAgentTestDoubles.NewRequest("10 minutes"));

        result.AgentSessionJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ASerializedSessionResumesOnTheNextTurn()
    {
        var coach = NewCoach(new ScriptedChatClient(DirectChangeJson));

        var first = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("10 minutes"));
        var second = await coach.RunTurnAsync(
            CoachAgentTestDoubles.NewRequest("and no audio", first.AgentSessionJson));

        second.Outcome.Should().Be(CoachAgentOutcome.Completed);
        second.AgentSessionJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnUnreadableSessionStartsAFreshConversationInsteadOfFailing()
    {
        var coach = NewCoach(new ScriptedChatClient(DirectChangeJson));

        var result = await coach.RunTurnAsync(
            CoachAgentTestDoubles.NewRequest("hello", agentSessionJson: "{ not json"));

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
    }

    [Fact]
    public async Task ToolsAreResolvedOncePerTurnFromTheCallingScope()
    {
        var toolFactory = new CoachAgentTestDoubles.StubToolFactory();
        var coach = CoachAgentTestDoubles.CreateCoach(
            CoachImplementation.Harness,
            new ScriptedChatClient(DirectChangeJson),
            toolFactory: toolFactory);

        await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));
        await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello again"));

        toolFactory.CreateCount.Should().Be(2, "no agent may cache another learner's tool instances");
    }

    [Fact]
    public async Task AHarnessAgentIsBuiltPerRunAndNoBaselineAgentIsBuilt()
    {
        var probe = CoachAgentTestDoubles.CountingFactory(new ScriptedChatClient(DirectChangeJson));
        var coach = CoachAgentTestDoubles.CreateCoach(
            CoachImplementation.Harness, new ScriptedChatClient(DirectChangeJson), agentFactoryProbe: probe);

        await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));
        await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello again"));

        probe.HarnessAgentsBuilt.Should().Be(2);
        probe.BaselineAgentsBuilt.Should().Be(0);
    }
}

/// <summary>A chat client that answers after a delay, so a timeout can be observed.</summary>
public sealed class SlowChatClient : IChatClient
{
    private readonly TimeSpan _delay;
    private readonly string _json;

    public SlowChatClient(TimeSpan delay, string json)
    {
        _delay = delay;
        _json = json;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, _json));
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
