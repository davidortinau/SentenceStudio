using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// The arm is a configuration choice, not a second code path. These tests prove the flag
/// selects the arm, that the default stays the baseline, and that a feature-off host builds
/// neither agent.
/// </summary>
public class CoachArmSelectionTests
{
    private static ServiceProvider BuildProvider(
        CoachOptions options,
        out CoachAgentTestDoubles.CountingAgentFactory probe,
        Microsoft.Extensions.AI.IChatClient? chatClient = null)
    {
        probe = CoachAgentTestDoubles.CountingFactory(
            chatClient ?? new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}"""), options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<CoachOptions>>(new TestOptionsMonitor<CoachOptions>(options));
        services.AddSingleton<ICoachAgentFactory>(probe);
        services.AddSingleton<ICoachToolFactory, CoachAgentTestDoubles.StubToolFactory>();
        services.AddSingleton<CoachTelemetry>();

        // TryAdd* in the production registration means the doubles above win.
        services.AddCoachBaseline();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheDefaultArmIsTheBaseline()
    {
        using var provider = BuildProvider(new CoachOptions { Enabled = true }, out _);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILearningCoach>()
            .Implementation.Should().Be(CoachImplementation.Baseline);
    }

    [Fact]
    public void TheFlagSelectsTheHarnessArm()
    {
        using var provider = BuildProvider(
            new CoachOptions { Enabled = true, Implementation = CoachImplementation.Harness }, out _);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILearningCoach>()
            .Implementation.Should().Be(CoachImplementation.Harness);
    }

    [Fact]
    public void BothArmsAreResolvableButOnlyOneServesTheInterface()
    {
        using var provider = BuildProvider(
            new CoachOptions { Enabled = true, Implementation = CoachImplementation.Harness }, out _);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<BaselineLearningCoach>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<HarnessLearningCoach>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ILearningCoach>().Should().BeOfType<HarnessLearningCoach>();
    }

    [Fact]
    public void TheArmIsScopedSoNoAgentOutlivesARequest()
    {
        var descriptor = new ServiceCollection().AddCoachBaseline()
            .Single(d => d.ServiceType == typeof(ILearningCoach));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public void ResolvingAnArmBuildsNoAgentAndCallsNoModel(CoachImplementation implementation)
    {
        var client = new RecordingChatClient();
        using var provider = BuildProvider(
            new CoachOptions { Enabled = false, Implementation = implementation }, out var probe, client);
        using var scope = provider.CreateScope();

        var coach = scope.ServiceProvider.GetRequiredService<ILearningCoach>();

        coach.Should().NotBeNull();
        probe.TotalAgentsBuilt.Should().Be(0, "a feature-off host must construct neither arm's agent");
        client.CallCount.Should().Be(0);
    }

    [Fact]
    public void ADefinedImplementationPassesStartupValidation()
    {
        var validator = new CoachImplementationAvailabilityValidator();

        validator.Validate(null, new CoachOptions { Implementation = CoachImplementation.Baseline })
            .Succeeded.Should().BeTrue();
        validator.Validate(null, new CoachOptions { Implementation = CoachImplementation.Harness })
            .Succeeded.Should().BeTrue("the harness arm now exists");
    }

    [Fact]
    public void AnUndefinedImplementationStopsTheHost()
    {
        var validator = new CoachImplementationAvailabilityValidator();

        var result = validator.Validate(null, new CoachOptions { Implementation = (CoachImplementation)9 });

        result.Failed.Should().BeTrue("a silent fallback would measure the same arm twice");
        result.FailureMessage.Should().Contain("baseline").And.Contain("harness");
    }

    [Fact]
    public void TheHarnessFlagIsReportedNotBlocked()
    {
        CoachApplicationServiceCollectionExtensions
            .RequiresHarnessArm(new CoachOptions { Implementation = CoachImplementation.Harness })
            .Should().BeTrue();
        CoachApplicationServiceCollectionExtensions
            .RequiresHarnessArm(new CoachOptions())
            .Should().BeFalse();
    }
}
