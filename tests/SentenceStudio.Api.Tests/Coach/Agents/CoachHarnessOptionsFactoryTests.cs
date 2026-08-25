using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// The configuration guard for the harness arm. Every optional harness capability must be
/// off, every experimental store or loop must stay unset, and the three configured values
/// must come from <see cref="CoachOptions"/>.
/// </summary>
public class CoachHarnessOptionsFactoryTests
{
    private static readonly string[] MustBeDisabled =
    [
        nameof(HarnessAgentOptions.DisableFileMemory),
        nameof(HarnessAgentOptions.DisableTodoProvider),
        nameof(HarnessAgentOptions.DisableAgentModeProvider),
        nameof(HarnessAgentOptions.DisableAgentSkillsProvider),
        nameof(HarnessAgentOptions.DisableWebSearch),
        nameof(HarnessAgentOptions.DisableToolAutoApproval)
    ];

    /// <summary>
    /// Capabilities that must stay unset. Each one would add a state store, a new tool
    /// surface, an unbounded loop, or silent history rewriting.
    /// </summary>
    /// <remarks>
    /// These are named as strings on purpose. Most of them carry
    /// <c>[Experimental("MAAI001")]</c>, so a <c>nameof</c> reference would be a compile-time
    /// use of an evaluation-only API and would need a suppression. The coach must not
    /// suppress that diagnostic, and the production factory never names these members at
    /// all. <see cref="EveryUnsetCapabilityStillExists"/> catches a rename.
    /// </remarks>
    private static readonly string[] MustStayUnset =
    [
        "FileAccessStore",
        "FileAccessProviderOptions",
        "FileMemoryStore",
        "BackgroundAgents",
        "BackgroundAgentsProviderOptions",
        "LoopEvaluators",
        "LoopAgentOptions",
        "CompactionStrategy",
        "MaxContextWindowTokens",
        "AgentSkillsSource",
        "AgentModeProviderOptions",
        "ToolApprovalAgentOptions",
        "ChatHistoryProvider",
        "AIContextProviders"
    ];

    private static HarnessAgentOptions Build(CoachOptions? options = null) =>
        CoachHarnessOptionsFactory.Create(options ?? new CoachOptions(), CoachAgentTestDoubles.StubTools());

    [Fact]
    public void EveryOptionalCapabilityIsDisabled()
    {
        var options = Build();

        foreach (var name in MustBeDisabled)
        {
            var value = typeof(HarnessAgentOptions).GetProperty(name)!.GetValue(options);
            value.Should().Be(true, "{0} must be off for a learner-facing planning turn", name);
        }
    }

    [Fact]
    public void EveryDisableFlagOnTheTypeIsAccountedFor()
    {
        // A new Disable* flag in a future package version must be reviewed, not inherited.
        var known = MustBeDisabled.Concat(
        [
            // Reviewed and deliberately left at the package default. Named as strings because
            // DisableCompaction is evaluation-only and must not be referenced in code.
            "DisableCompaction",
            nameof(HarnessAgentOptions.DisableOpenTelemetry),
            nameof(HarnessAgentOptions.DisableApprovalNotRequiredFunctionBypassing),
            nameof(HarnessAgentOptions.DisableApprovalResponseBinding)
        ]).ToHashSet(StringComparer.Ordinal);

        var actual = typeof(HarnessAgentOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.StartsWith("Disable", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();

        actual.Should().BeSubsetOf(known,
            "a new harness capability flag must be reviewed before the coach inherits its default");
    }

    [Fact]
    public void EveryExperimentalStoreAndLoopStaysUnset()
    {
        var options = Build();

        foreach (var name in MustStayUnset)
        {
            Read(options, name).Should()
                .BeNull("{0} must stay unset so the harness wires no extra capability", name);
        }
    }

    [Fact]
    public void EveryUnsetCapabilityStillExists()
    {
        foreach (var name in MustStayUnset)
        {
            typeof(HarnessAgentOptions).GetProperty(name).Should()
                .NotBeNull("{0} was renamed or removed, so the guard no longer covers it", name);
        }
    }

    [Fact]
    public void TheProductionFactoryNamesNoEvaluationOnlyMember()
    {
        // The API project builds with no MAAI001 suppression anywhere, so this holds by
        // construction. The assertion documents the rule next to the guard it protects.
        var factory = typeof(CoachHarnessOptionsFactory);

        factory.GetCustomAttributes(inherit: false)
            .Should().NotContain(a => a.GetType().Name.Contains("Experimental", StringComparison.Ordinal));
    }

    private static object? Read(HarnessAgentOptions options, string propertyName) =>
        typeof(HarnessAgentOptions).GetProperty(propertyName)?.GetValue(options);

    [Fact]
    public void HarnessInstructionsAreEmptyNotNull()
    {
        var options = Build();

        options.HarnessInstructions.Should().NotBeNull("null would apply HarnessAgent.DefaultInstructions");
        options.HarnessInstructions.Should().BeEmpty();
    }

    [Fact]
    public void TheAgentNameAndInstructionsMatchTheBaselineArm()
    {
        var options = Build();

        options.Name.Should().Be(CoachInstructions.AgentName);
        options.Description.Should().Be(CoachInstructions.AgentDescription);
        options.ChatOptions!.Instructions.Should().Be(CoachInstructions.Instructions);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(3)]
    public void TheIterationCapComesFromConfiguration(int iterations)
    {
        var options = Build(new CoachOptions { MaxIterationsPerRequest = iterations });

        options.MaximumIterationsPerRequest.Should().Be(iterations);
    }

    [Fact]
    public void TheDefaultIterationCapIsSix()
    {
        Build().MaximumIterationsPerRequest.Should().Be(6);
        new CoachOptions().MaxIterationsPerRequest.Should().Be(6);
    }

    [Fact]
    public void TheOutputTokenCapIsOnChatOptionsAndComesFromConfiguration()
    {
        var options = Build(new CoachOptions { MaxOutputTokens = 900 });

        options.ChatOptions!.MaxOutputTokens.Should().Be(900);

        // Read by reflection: the harness-level MaxOutputTokens is evaluation-only, and the
        // cap belongs on the per-response ChatOptions rather than a model-capability property.
        Read(options, "MaxOutputTokens").Should().BeNull();
    }

    [Fact]
    public void TheToolsAreExactlyTheFiveReadOnlyTools()
    {
        var options = Build();

        options.ChatOptions!.Tools!.Select(t => t.Name)
            .Should().BeEquivalentTo(SentenceStudio.Api.Coach.Tools.CoachToolNames.All);
    }

    [Fact]
    public void NoToolNamesAWriteAction()
    {
        var options = Build();

        foreach (var tool in options.ChatOptions!.Tools!)
        {
            tool.Should().BeAssignableTo<AIFunction>();
            tool.Name.Should().NotContainAny("write", "update", "delete", "apply", "save", "create");
        }
    }

    [Fact]
    public void TheSamplingKnobsAreLeftAtTheProviderDefault()
    {
        var options = Build().ChatOptions!;

        // Regression: an explicit Temperature = 0 made every real run fail with HTTP 400
        // "Parameter: temperature. Unsupported value: temperature does not support 0 with
        // this model. Only the default (1) value is supported." on gpt-5-mini. The coach
        // sets no sampling value at all, so each provider applies its own default.
        options.Temperature.Should().BeNull();
        options.TopP.Should().BeNull();
        options.TopK.Should().BeNull();
        options.FrequencyPenalty.Should().BeNull();
        options.PresencePenalty.Should().BeNull();
    }
}
