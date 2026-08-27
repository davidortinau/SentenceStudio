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

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Reading the model's answer.
/// </summary>
/// <remarks>
/// <para>
/// Regression cover for an Aspire E2E failure. A first turn ("make today's plan 5 minutes and
/// no audio") applied and persisted revision 1. The second turn ("could you suggest one useful
/// change") called the real model, consumed usage, incremented <c>TurnCount</c> to 2 — and
/// came back Incomplete with "I could not finish that." No exception, no timeout, and
/// <c>StopReason</c> on the session row stayed null.
/// </para>
/// <para>
/// <b>The branch.</b> That sentence is only reachable from the incomplete path, so the turn did
/// not complete. Every failure branch in the turn runner returns a null
/// <c>AgentSessionJson</c>, and the reducer only increments <c>TurnCount</c> when that value is
/// non-null — so a <c>TurnCount</c> of 2 rules out every catch branch, the timeout, and the
/// cancel. Exactly one outcome both increments the turn and does not complete:
/// <see cref="CoachAgentOutcome.InvalidOutput"/>, mapping to
/// <see cref="CoachStopReason.ValidationFailed"/>. The model answered; the answer did not
/// deserialize into a turn intent.
/// </para>
/// <para>
/// <b>Why the second turn and not the first.</b> The response schema is derived from
/// <c>CoachTurnIntent</c>, but it is a request rather than a constraint — the deployed model is
/// not run in strict structured-output mode. A trivial turn answers with the bare object; a
/// suggestion turn calls tools first and answers with their results in context, and that is
/// where a model wraps the object in a <c>```json</c> fence or introduces it with a sentence.
/// </para>
/// </remarks>
public class CoachAgentOutputRecoveryTests
{
    private const string ValidIntentJson =
        """{"Kind":"SuggestConstraintChange","ConstraintDelta":{"SkillEmphasis":"Writing"},"CoachMessage":"A short writing block would balance today."}""";

    // ---------------------------------------------------------------- shapes that now parse

    [Fact]
    public async Task ABareIntentObject_IsRead()
    {
        var result = await RunAsync(ValidIntentJson);

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.Kind.Should().Be(CoachIntentKind.SuggestConstraintChange);
    }

    [Fact]
    public async Task AFencedJsonBlock_IsRead()
    {
        var result = await RunAsync($"```json\n{ValidIntentJson}\n```");

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.ConstraintDelta!.SkillEmphasis.Should().Be(CoachSkillEmphasis.Writing);
    }

    [Fact]
    public async Task AnIntroducedObject_IsRead()
    {
        var result = await RunAsync($"Sure — here is my suggestion:\n\n{ValidIntentJson}");

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.Kind.Should().Be(CoachIntentKind.SuggestConstraintChange);
    }

    [Fact]
    public async Task AnObjectWithTrailingProse_IsRead()
    {
        var result = await RunAsync($"{ValidIntentJson}\n\nLet me know if you want something else.");

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
    }

    [Fact]
    public async Task ABraceInsideAStringValue_DoesNotEndTheObjectEarly()
    {
        const string json =
            """{"Kind":"NoChange","CoachMessage":"I use { and } in text \"safely\"","EvidenceReferences":[]}""";

        var result = await RunAsync($"```\n{json}\n```");

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.CoachMessage.Should().Contain("{");
    }

    // ---------------------------------------------------------------- shapes still refused

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I think you should do some writing today.")]
    [InlineData("null")]
    [InlineData("[]")]
    // A response cut off by the output-token cap: the object never closes.
    [InlineData("""{"Kind":"SuggestConstraintChange","ConstraintDelta":{"SkillEmphasis":""")]
    public async Task AnUnreadableAnswer_IsStillRefusedAndCarriesNoIntent(string payload)
    {
        var result = await RunAsync(payload);

        result.Outcome.Should().Be(CoachAgentOutcome.InvalidOutput);
        result.Intent.Should().BeNull();

        // The conversation is still resumable, which is what increments TurnCount and is how
        // this branch was identified from the live session row.
        result.AgentSessionJson.Should().NotBeNull();
    }

    [Fact]
    public void TheExtractorReturnsNullRatherThanGuessingAtAnUnbalancedObject()
    {
        CoachAgentTurnRunner.TryExtractJsonObject("""{"Kind":"NoChange" """).Should().BeNull();
        CoachAgentTurnRunner.TryExtractJsonObject("no braces here").Should().BeNull();
        CoachAgentTurnRunner.TryExtractJsonObject(null).Should().BeNull();
        CoachAgentTurnRunner.TryExtractJsonObject("{}").Should().Be("{}");
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public async Task TheReportedSequence_NowProducesAPendingSuggestion()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // Turn 1: the direct change that succeeded in the live run.
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.DirectConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 5, AudioAllowed = false },
                CoachMessage = "Done."
            },
            AgentSessionJson = """{"turn":1}"""
        };

        var first = await SubmitAsync(harness, sessionId, "Make today\u2019s plan 5 minutes and no audio.");
        first.Value!.ChangeReceipt.Should().NotBeNull();
        harness.Db.CoachPlanRevisions.Should().HaveCount(1);

        // Turn 2: the suggestion turn, answered in a fenced block — the shape that used to be
        // thrown away after the run had already been paid for. The planner offers a different
        // writing block than the one already on the plan, so the suggestion has real effect.
        harness.PlanService.NextRemainder =
        [
            new SentenceStudio.Services.Plans.PlanSnapshotItem
            {
                PlanItemId = "suggested-writing",
                ActivityType = nameof(SentenceStudio.Services.Progress.PlanActivityType.Writing),
                ResourceId = "resource-suggested-writing",
                Priority = 1,
                EstimatedMinutes = 5
            }
        ];

        harness.Coach.NextResult = await ModelAnswerAsync($"```json\n{ValidIntentJson}\n```");

        var second = await SubmitAsync(harness, sessionId, "Could you suggest one useful change to today\u2019s plan?");

        second.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        second.Value.PendingSuggestion.Should().NotBeNull();
        second.Value.PendingSuggestion!.Delta.SkillEmphasis.Should().Be(CoachSkillEmphasis.Writing);
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AnUnreadableAnswer_WritesNothingAndKeepsAnOpenSuggestion()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { SkillEmphasis = CoachSkillEmphasis.Writing },
                CoachMessage = "Suggestion."
            },
            AgentSessionJson = """{"turn":1}"""
        };
        var offered = (await SubmitAsync(harness, sessionId, "suggest something")).Value!.PendingSuggestion!;

        harness.Coach.NextResult = await ModelAnswerAsync("I think you should do some writing today.");

        var result = await SubmitAsync(harness, sessionId, "tell me more");

        result.Value!.Status.Should().Be(CoachTurnStatus.Incomplete);
        result.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        result.Value.PendingSuggestion!.SuggestionId.Should().Be(offered.SuggestionId);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnIncompleteTurn_RecordsWhyItEndedOnTheSession()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Db.CoachSessions.Single().StopReason.Should().BeNull();

        harness.Coach.NextResult = await ModelAnswerAsync("no json at all");
        var result = await SubmitAsync(harness, sessionId, "suggest something");

        result.Value!.StopReason.Should().Be(CoachStopReason.ValidationFailed);

        // The live session row kept a null StopReason, so nothing about the failure survived
        // the request. It does now.
        harness.Db.CoachSessions.Single().StopReason.Should().Be(CoachStopReason.ValidationFailed);
    }

    [Fact]
    public async Task ARefusedAnswer_ReadsDifferentlyFromARunThatNeverFinished()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = await ModelAnswerAsync("no json at all");
        var refused = await SubmitAsync(harness, sessionId, "suggest something");

        harness.Coach.NextResult = CoachAgentTurnResult.Failure(CoachAgentOutcome.Timeout, "timed out");
        var timedOut = await SubmitAsync(harness, sessionId, "suggest something");

        // R5: neutral copy replaces Plan-biased wording per Zoe-approved design.
        refused.Value!.Messages.Single().Text.Should().Be(CoachDeterministicCopy.ValidationFailedNeutral);
        timedOut.Value!.Messages.Single().Text.Should().Be(CoachDeterministicCopy.IncompleteNeutral);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });

    /// <summary>Runs the real turn runner over a scripted model answer.</summary>
    private static async Task<CoachAgentTurnResult> RunAsync(string payload)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new ScriptedChatClient(payload));

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

    /// <summary>The turn result the real runner produces for a given model answer.</summary>
    private static Task<CoachAgentTurnResult> ModelAnswerAsync(string payload) => RunAsync(payload);

    private sealed class StubToolFactory : ICoachToolFactory
    {
        public IReadOnlyList<AIFunction> CreateTools() =>
            CoachToolNames.All
                .Select(name => AIFunctionFactory.Create(
                    () => "stub",
                    new AIFunctionFactoryOptions { Name = name, Description = $"Reads {name}." }))
                .ToList();
    }
}
