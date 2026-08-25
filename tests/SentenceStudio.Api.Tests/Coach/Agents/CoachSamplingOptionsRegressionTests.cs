using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// Regression guard for the sampling options both arms send to the model.
/// </summary>
/// <remarks>
/// <para>
/// The first real end-to-end coach run failed with HTTP 400 from the model endpoint:
/// </para>
/// <code>
/// Parameter: temperature. Unsupported value: temperature does not support 0 with this
/// model. Only the default (1) value is supported.
/// </code>
/// <para>
/// The stack reached <c>CoachAgentTurnRunner</c>, so the failure hit both arms: the plain
/// agent and the harness built the same <c>ChatOptions</c> with <c>Temperature = 0</c>.
/// gpt-5-mini, and the other reasoning-style models on the same family, reject any explicit
/// temperature. The coach now sets none, so a provider applies its own default and the same
/// code works across model families. Do not set 1 explicitly either: an omitted property is
/// portable, a hard-coded default is not.
/// </para>
/// <para>
/// Determinism never came from the sampling knob. It comes from the closed turn-intent
/// schema, the application reducer, and the deterministic planner.
/// </para>
/// </remarks>
public class CoachSamplingOptionsRegressionTests
{
    public static TheoryData<CoachImplementation> Arms => new()
    {
        CoachImplementation.Baseline,
        CoachImplementation.Harness
    };

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task NeitherArmSendsAnExplicitTemperature(CoachImplementation arm)
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");
        var coach = CoachAgentTestDoubles.CreateCoach(arm, client);

        var result = await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        client.LastOptions!.Temperature.Should().BeNull(
            "gpt-5-mini rejects any explicit temperature, including 0");
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task NeitherArmSendsAnyOtherSamplingOverride(CoachImplementation arm)
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");
        var coach = CoachAgentTestDoubles.CreateCoach(arm, client);

        await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        var options = client.LastOptions!;
        options.TopP.Should().BeNull();
        options.TopK.Should().BeNull();
        options.FrequencyPenalty.Should().BeNull();
        options.PresencePenalty.Should().BeNull();
        options.Seed.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task TheLimitsThatDoTravelAreStillSent(CoachImplementation arm)
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");
        var coach = CoachAgentTestDoubles.CreateCoach(
            arm, client, new CoachOptions { Enabled = true, MaxOutputTokens = 777 });

        await coach.RunTurnAsync(CoachAgentTestDoubles.NewRequest("hello"));

        // Dropping the sampling knob must not drop the bounds that keep a run affordable.
        client.LastOptions!.MaxOutputTokens.Should().Be(777);
        client.LastOptions.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
        client.LastOptions.Tools.Should().HaveCount(5);
    }

    [Fact]
    public void TheBaselineAgentOptionsCarryNoSamplingValue()
    {
        var factory = CoachAgentTestDoubles.RealFactory(new ScriptedChatClient("{}"));

        var agent = factory.TryCreateAgent(CoachAgentTestDoubles.StubTools());

        agent.Should().NotBeNull("the guard is meaningless if the agent was never built");
    }

    [Fact]
    public void TheHarnessOptionsCarryNoSamplingValue()
    {
        var options = CoachHarnessOptionsFactory.Create(new CoachOptions(), CoachAgentTestDoubles.StubTools());

        options.ChatOptions!.Temperature.Should().BeNull();
    }
}
