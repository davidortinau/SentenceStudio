using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// The tool allow-list is an active gate, not a registration. These tests prove both arms
/// run it before an agent exists, and that a rejected set stops the run with no model call.
/// </summary>
public class CoachToolAllowListGateTests
{
    public static TheoryData<CoachImplementation> Arms => new()
    {
        CoachImplementation.Baseline,
        CoachImplementation.Harness
    };

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task AnExtraToolStopsTheRunBeforeAnyModelCall(CoachImplementation arm)
    {
        var client = new RecordingChatClient();
        var coach = CoachAgentTestDoubles.CreateCoach(
            arm, client, toolFactory: new TamperedToolFactory(TamperKind.ExtraTool));

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.Failed);
        result.Intent.Should().BeNull();
        result.FailureReason.Should().Contain("safety contract");
        client.CallCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task AWriteShapedToolStopsTheRunBeforeAnyModelCall(CoachImplementation arm)
    {
        var client = new RecordingChatClient();
        var coach = CoachAgentTestDoubles.CreateCoach(
            arm, client, toolFactory: new TamperedToolFactory(TamperKind.WriteTool));

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.Failed);
        client.CallCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task AMissingToolStopsTheRunBeforeAnyModelCall(CoachImplementation arm)
    {
        var client = new RecordingChatClient();
        var coach = CoachAgentTestDoubles.CreateCoach(
            arm, client, toolFactory: new TamperedToolFactory(TamperKind.MissingTool));

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.Failed);
        client.CallCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task AToolThatAcceptsAUserArgumentStopsTheRunBeforeAnyModelCall(CoachImplementation arm)
    {
        var client = new RecordingChatClient();
        var coach = CoachAgentTestDoubles.CreateCoach(
            arm, client, toolFactory: new TamperedToolFactory(TamperKind.IdentityArgument));

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.Failed);
        client.CallCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task TheApprovedSetStillRuns(CoachImplementation arm)
    {
        var coach = CoachAgentTestDoubles.CreateCoach(
            arm, new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}"""));

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
    }

    [Fact]
    public void TheFactoryRejectsATamperedSetForBothCreatePaths()
    {
        var factory = CoachAgentTestDoubles.RealFactory(new ScriptedChatClient("{}"));
        var tampered = new TamperedToolFactory(TamperKind.ExtraTool).CreateTools();

        factory.Invoking(f => f.TryCreateAgent(tampered))
            .Should().Throw<CoachContractViolationException>()
            .Which.Contract.Should().Be("coach tool allow-list");

        factory.Invoking(f => f.TryCreateHarnessAgent(tampered))
            .Should().Throw<CoachContractViolationException>();
    }

    private enum TamperKind
    {
        ExtraTool,
        WriteTool,
        MissingTool,
        IdentityArgument
    }

    /// <summary>Produces a tool set that breaks one allow-list rule.</summary>
    private sealed class TamperedToolFactory : ICoachToolFactory
    {
        private readonly TamperKind _kind;

        public TamperedToolFactory(TamperKind kind) => _kind = kind;

        public IReadOnlyList<AIFunction> CreateTools()
        {
            var approved = CoachAgentTestDoubles.StubTools().ToList();

            return _kind switch
            {
                TamperKind.ExtraTool =>
                    [.. approved, Stub("read_other_learner_notes")],
                TamperKind.WriteTool =>
                    [.. approved, Stub("apply_plan_update")],
                TamperKind.MissingTool =>
                    approved.Where(t => t.Name != CoachToolNames.PreviewPracticePlan).ToList(),
                TamperKind.IdentityArgument =>
                    [
                        .. approved.Where(t => t.Name != CoachToolNames.GetPracticeBalance),
                        AIFunctionFactory.Create(
                            (string userProfileId) => userProfileId,
                            CoachToolNames.GetPracticeBalance,
                            "Reads the balance for a named learner.")
                    ],
                _ => approved
            };
        }

        private static AIFunction Stub(string name) =>
            AIFunctionFactory.Create(() => "stub", new AIFunctionFactoryOptions { Name = name, Description = name });
    }
}
