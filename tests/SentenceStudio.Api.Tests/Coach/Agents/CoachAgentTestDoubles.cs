using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// Shared doubles for the arm tests, so the baseline and the harness are exercised through
/// exactly the same wiring and the same recorded model output.
/// </summary>
internal static class CoachAgentTestDoubles
{
    /// <summary>
    /// Tools with the production names and closed schemas but no data access, so agent
    /// wiring can be asserted without a database.
    /// </summary>
    public static IReadOnlyList<AIFunction> StubTools() =>
        CoachToolNames.All
            .Select(name => AIFunctionFactory.Create(
                () => "stub",
                new AIFunctionFactoryOptions { Name = name, Description = $"Reads {name}." }))
            .ToList();

    public sealed class StubToolFactory : ICoachToolFactory
    {
        public int CreateCount { get; private set; }

        public IReadOnlyList<AIFunction> CreateTools()
        {
            CreateCount++;
            return StubTools();
        }
    }

    /// <summary>
    /// Records whether an agent was ever built, so a feature-off or model-free path can be
    /// proven to construct neither arm's agent.
    /// </summary>
    public sealed class CountingAgentFactory : ICoachAgentFactory
    {
        private readonly ICoachAgentFactory _inner;

        public CountingAgentFactory(ICoachAgentFactory inner) => _inner = inner;

        public int BaselineAgentsBuilt { get; private set; }
        public int HarnessAgentsBuilt { get; private set; }
        public int TotalAgentsBuilt => BaselineAgentsBuilt + HarnessAgentsBuilt;

        public bool IsModelAvailable => _inner.IsModelAvailable;

        public Microsoft.Agents.AI.AIAgent? TryCreateAgent(IReadOnlyList<AIFunction> tools)
        {
            var agent = _inner.TryCreateAgent(tools);
            if (agent is not null)
            {
                BaselineAgentsBuilt++;
            }
            return agent;
        }

        public Microsoft.Agents.AI.AIAgent? TryCreateHarnessAgent(IReadOnlyList<AIFunction> tools)
        {
            var agent = _inner.TryCreateHarnessAgent(tools);
            if (agent is not null)
            {
                HarnessAgentsBuilt++;
            }
            return agent;
        }
    }

    /// <summary>A real agent factory over the supplied client, for gate tests.</summary>
    public static CoachAgentFactory RealFactory(IChatClient? chatClient, CoachOptions? coachOptions = null)
    {
        var services = new ServiceCollection();
        if (chatClient is not null)
        {
            services.AddSingleton(chatClient);
        }

        return new CoachAgentFactory(
            services.BuildServiceProvider(),
            new TestOptionsMonitor<CoachOptions>(coachOptions ?? new CoachOptions { Enabled = true }),
            NullLoggerFactory.Instance);
    }

    /// <summary>Builds one arm over a chat client, or over no client at all.</summary>
    public static ILearningCoach CreateCoach(
        CoachImplementation implementation,
        IChatClient? chatClient,
        CoachOptions? coachOptions = null,
        ICoachToolFactory? toolFactory = null,
        CountingAgentFactory? agentFactoryProbe = null)
    {
        var services = new ServiceCollection();
        if (chatClient is not null)
        {
            services.AddSingleton(chatClient);
        }

        var provider = services.BuildServiceProvider();
        var options = new TestOptionsMonitor<CoachOptions>(coachOptions ?? new CoachOptions { Enabled = true });
        ICoachAgentFactory factory = new CoachAgentFactory(provider, options, NullLoggerFactory.Instance);

        if (agentFactoryProbe is not null)
        {
            factory = agentFactoryProbe;
        }

        ICoachToolFactory tools = toolFactory ?? new StubToolFactory();

        return implementation == CoachImplementation.Harness
            ? new HarnessLearningCoach(factory, tools, options, new CoachTelemetry(),
                NullLogger<HarnessLearningCoach>.Instance)
            : new BaselineLearningCoach(factory, tools, options, new CoachTelemetry(),
                NullLogger<BaselineLearningCoach>.Instance);
    }

    /// <summary>Wraps a real factory so the probe can count agents while still building them.</summary>
    public static CountingAgentFactory CountingFactory(IChatClient? chatClient, CoachOptions? coachOptions = null)
    {
        var services = new ServiceCollection();
        if (chatClient is not null)
        {
            services.AddSingleton(chatClient);
        }

        var provider = services.BuildServiceProvider();
        var options = new TestOptionsMonitor<CoachOptions>(coachOptions ?? new CoachOptions { Enabled = true });
        return new CountingAgentFactory(new CoachAgentFactory(provider, options, NullLoggerFactory.Instance));
    }

    public static CoachAgentTurnRequest NewRequest(string text, string? agentSessionJson = null) => new()
    {
        SessionId = "session-1",
        AgentSessionJson = agentSessionJson,
        LearnerText = text,
        ActiveConstraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 20,
            AudioAllowed = true,
            SpeechAllowed = true,
            TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        },
        ClarificationsRemaining = 2,
        UserLocalDate = new DateOnly(2026, 8, 14)
    };
}
