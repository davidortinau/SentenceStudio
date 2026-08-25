using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The coach's output budget and reasoning policy.
/// </summary>
/// <remarks>
/// <para>
/// Regression cover for a live baseline failure. The first, trivial constraint turn succeeded;
/// the tool-using suggestion turn came back <c>FinishReason=length, TextLength=0,
/// ContainsJsonObject=False</c>. Nothing was malformed — gpt-5-mini spent the whole 1,200-token
/// cap on hidden reasoning and never emitted a visible answer.
/// </para>
/// <para>
/// Microsoft Learn is explicit that <c>max_completion_tokens</c> covers "reasoning tokens,
/// visible output tokens, and formatting tokens", and that exhausting it "can occur before the
/// model produces any visible output. You pay for input and reasoning tokens but receive no
/// answer." (<c>https://learn.microsoft.com/azure/foundry/openai/how-to/reasoning</c>)
/// </para>
/// </remarks>
public class CoachOutputBudgetTests
{
    // ---------------------------------------------------------------- configured policy

    [Fact]
    public void TheCoachAsksForMinimalReasoningByDefault()
    {
        var options = Build(new CoachOptions());

#pragma warning disable OPENAI001
        var reasoning = ReadReasoningEffort(options);

        // A coach turn is bounded classification and extraction against a closed schema, so it
        // wants the least reasoning the model offers. GPT-5 models accept 'minimal'.
        reasoning.Should().Be(ChatReasoningEffortLevel.Minimal);
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void TheConfiguredReasoningEffortIsSent(string effort)
    {
        var options = Build(new CoachOptions { ReasoningEffort = effort });

        ReadReasoningEffort(options).Should().NotBeNull();
    }

    [Fact]
    public void AnEmptyReasoningEffortSendsNoReasoningParameterAtAll()
    {
        // Some deployments are not reasoning models. Omitting the parameter is the portable
        // choice; sending an unsupported one is a 400.
        var options = Build(new CoachOptions { ReasoningEffort = "" });

        options.RawRepresentationFactory.Should().BeNull();
    }

    [Fact]
    public void AnUnknownReasoningEffortFailsStartupRatherThanBeingDroppedSilently()
    {
        var result = new CoachOptionsValidator().Validate(null, Valid(o => o.ReasoningEffort = "exhaustive"));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ReasoningEffort");
    }

    [Fact]
    public void TheOutputCapIsSentAndIsLargeEnoughToSurviveHiddenReasoning()
    {
        var options = Build(new CoachOptions());

        options.MaxOutputTokens.Should().Be(16_000);

        // The cap is a total-generation budget. The typed intent itself is small — the schema
        // caps the coach message at 400 characters — so the headroom is for reasoning and
        // tool-call round trips, and tokens are billed on what is generated, not on the cap.
        options.MaxOutputTokens.Should().BeGreaterThan(CoachOptionsValidator.MinOutputTokens);
    }

    [Fact]
    public void TheCapIsNeverRemoved()
    {
        Build(new CoachOptions()).MaxOutputTokens.Should().NotBeNull();
        Build(new CoachOptions { MaxOutputTokens = 4_000 }).MaxOutputTokens.Should().Be(4_000);
    }

    [Fact]
    public void ACapSmallEnoughToReproduceTheDefectIsRejectedAtStartup()
    {
        // 1,200 was the value that produced an empty answer on a reasoning model. The floor
        // now sits above anything that can do that again.
        CoachOptionsValidator.MinOutputTokens.Should().BeGreaterThan(1_200);

        new CoachOptionsValidator().Validate(null, Valid(o => o.MaxOutputTokens = 1_200))
            .Failed.Should().BeTrue();
    }

    // ---------------------------------------------------------------- arm parity

    [Fact]
    public void BothArmsSendTheSameLimitsAndTheSameReasoningPolicy()
    {
        var options = new CoachOptions { MaxOutputTokens = 9_000, ReasoningEffort = "low" };
        var tools = StubTools();

        var baseline = CoachChatOptionsFactory.Create(options, tools);
        var harness = CoachHarnessOptionsFactory.Create(options, tools).ChatOptions!;

        harness.MaxOutputTokens.Should().Be(baseline.MaxOutputTokens);
        harness.Instructions.Should().Be(baseline.Instructions);
        harness.Tools!.Select(t => t.Name).Should().BeEquivalentTo(baseline.Tools!.Select(t => t.Name));
        ReadReasoningEffort(harness).Should().Be(ReadReasoningEffort(baseline));
    }

    [Fact]
    public void NeitherArmSetsTemperature()
    {
        // gpt-5-mini answers HTTP 400 for any explicit temperature.
        var options = new CoachOptions();

        CoachChatOptionsFactory.Create(options, StubTools()).Temperature.Should().BeNull();
        CoachHarnessOptionsFactory.Create(options, StubTools()).ChatOptions!.Temperature.Should().BeNull();
    }

    // ---------------------------------------------------------------- the length stop

    [Fact]
    public async Task AResponseThatStopsAtTheCap_IsReportedAsAnOutputLimit()
    {
        // Exactly the live shape: stopped on length, no visible text at all.
        var result = await RunAsync(new ScriptedChatClient("") { FinishReason = ChatFinishReason.Length });

        result.Outcome.Should().Be(CoachAgentOutcome.OutputLimitReached);
        result.Intent.Should().BeNull();
        result.FailureReason.Should().Contain("output token limit");
    }

    [Fact]
    public async Task ATruncatedButNonEmptyAnswer_IsAlsoAnOutputLimit()
    {
        var client = new ScriptedChatClient("""{"Kind":"SuggestConstraintChange","ConstraintDelta":{"Skill""")
        {
            FinishReason = ChatFinishReason.Length
        };

        (await RunAsync(client)).Outcome.Should().Be(CoachAgentOutcome.OutputLimitReached);
    }

    [Fact]
    public async Task AMalformedAnswerThatDidNotHitTheCap_IsStillASchemaProblem()
    {
        // The two failures have different fixes, so they must stay distinguishable.
        var result = await RunAsync(new ScriptedChatClient("I think you should do some writing."));

        result.Outcome.Should().Be(CoachAgentOutcome.InvalidOutput);
    }

    [Fact]
    public async Task AValidAnswerThatFilledTheCap_IsStillRead()
    {
        // FinishReason only decides how a failure is described; it never discards a readable
        // answer.
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""")
        {
            FinishReason = ChatFinishReason.Length
        };

        var result = await RunAsync(client);

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.Kind.Should().Be(CoachIntentKind.NoChange);
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public async Task AnOutputLimitSurfacesItsOwnStopReasonAndWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = await RunAsync(
            new ScriptedChatClient("") { FinishReason = ChatFinishReason.Length });

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Could you suggest one useful change to today\u2019s plan?"
        });

        result.Value!.Status.Should().Be(CoachTurnStatus.Incomplete);
        result.Value.StopReason.Should().Be(CoachStopReason.OutputTokenLimit);
        result.Value.Messages.Single().Text.Should().Contain("ran out of room");

        // The learner's plan is untouched and the session records why the turn ended.
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.Db.CoachSessions.Single().StopReason.Should().Be(CoachStopReason.OutputTokenLimit);
    }

    [Fact]
    public async Task AnOutputLimitReadsDifferentlyFromAnUnreadableAnswer()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = await RunAsync(
            new ScriptedChatClient("") { FinishReason = ChatFinishReason.Length });
        var capped = await SubmitAsync(harness, sessionId);

        harness.Coach.NextResult = await RunAsync(new ScriptedChatClient("just prose"));
        var malformed = await SubmitAsync(harness, sessionId);

        capped.Value!.StopReason.Should().Be(CoachStopReason.OutputTokenLimit);
        malformed.Value!.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        capped.Value.Messages.Single().Text.Should().NotBe(malformed.Value.Messages.Single().Text);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitAsync(
        CoachApplicationHarness harness, string sessionId) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "suggest something"
        });

    private static ChatOptions Build(CoachOptions options) =>
        CoachChatOptionsFactory.Create(options, StubTools());

    private static IReadOnlyList<AIFunction> StubTools() =>
        CoachToolNames.All
            .Select(name => AIFunctionFactory.Create(
                () => "stub", new AIFunctionFactoryOptions { Name = name, Description = $"Reads {name}." }))
            .ToList();

    /// <summary>
    /// Reads the OpenAI-specific reasoning level back off the options the coach would send.
    /// </summary>
#pragma warning disable OPENAI001
    private static ChatReasoningEffortLevel? ReadReasoningEffort(ChatOptions options)
    {
        var raw = options.RawRepresentationFactory?.Invoke(null!);
#pragma warning disable OPENAI001
        return (raw as ChatCompletionOptions)?.ReasoningEffortLevel;
    }
#pragma warning restore OPENAI001

    private static CoachOptions Valid(Action<CoachOptions> configure)
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = { "learner-1" }
        };

        configure(options);
        return options;
    }

    private static async Task<CoachAgentTurnResult> RunAsync(IChatClient client)
    {
        var services = new ServiceCollection();
        services.AddSingleton(client);

        using var provider = services.BuildServiceProvider();
        var options = new TestOptionsMonitor<CoachOptions>(new CoachOptions { Enabled = true });
        using var telemetry = new CoachTelemetry();

        var coach = new BaselineLearningCoach(
            new CoachAgentFactory(provider, options, NullLoggerFactory.Instance),
            new StubToolFactory(),
            options,
            telemetry,
            NullLogger<BaselineLearningCoach>.Instance);

        return await coach.RunTurnAsync(new CoachAgentTurnRequest
        {
            SessionId = "session-1",
            LearnerText = "Could you suggest one useful change to today\u2019s plan?",
            ActiveConstraints = new CoachConstraintSetDto
            {
                AvailableMinutes = 5,
                AudioAllowed = false,
                SpeechAllowed = true,
                TypingAllowed = true,
                EnergyLevel = CoachEnergyLevel.Normal
            },
            ClarificationsRemaining = 2,
            UserLocalDate = new DateOnly(2026, 8, 15)
        });
    }

    private sealed class StubToolFactory : ICoachToolFactory
    {
        public IReadOnlyList<AIFunction> CreateTools() => StubTools();
    }
}
