using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// Builds the restricted <see cref="HarnessAgentOptions"/> for the harness arm.
/// </summary>
/// <remarks>
/// <para>
/// The harness ships an opinionated, batteries-included pipeline: file memory, a todo
/// provider, agent modes, skills discovery, web search, tool auto-approval, background
/// agents, loop evaluators, and compaction. None of that is appropriate for a learner-facing
/// planning turn that must stay bounded, auditable, and free of ambient state, so this
/// factory turns every optional capability off and leaves the rest of the experimental
/// surface unset.
/// </para>
/// <para>
/// Only three values come from configuration: the instruction text, the output-token cap, and
/// the iteration cap. Everything else is a fixed policy so an operator cannot widen the
/// harness by editing <c>appsettings</c>.
/// </para>
/// <para>
/// Unset by design: <c>FileAccessStore</c>, <c>FileAccessProviderOptions</c>,
/// <c>FileMemoryStore</c>, <c>BackgroundAgents</c>, <c>BackgroundAgentsProviderOptions</c>,
/// <c>LoopEvaluators</c>, <c>LoopAgentOptions</c>, <c>CompactionStrategy</c>,
/// <c>MaxContextWindowTokens</c>, <c>AgentSkillsSource</c>, <c>AgentModeProviderOptions</c>,
/// <c>ToolApprovalAgentOptions</c>, <c>ChatHistoryProvider</c>, and <c>AIContextProviders</c>.
/// A null there means "no such capability is wired", which is the safe reading; setting any
/// of them would add a new state store, a new tool surface, or an unbounded loop.
/// </para>
/// </remarks>
public static class CoachHarnessOptionsFactory
{
    /// <summary>
    /// The harness-level instruction text. Empty is deliberate: the coach's own developer
    /// instructions already state the boundaries, and the harness defaults describe general
    /// tool use, planning, and file work that does not apply to a one-turn planning agent.
    /// The harness concatenates harness instructions before agent instructions, so leaving
    /// the default in place would prepend guidance the coach must not follow.
    /// </summary>
    public const string HarnessInstructions = "";

    /// <summary>Builds the options for one run over the supplied per-request tools.</summary>
    public static HarnessAgentOptions Create(CoachOptions options, IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tools);

        return new HarnessAgentOptions
        {
            Name = CoachInstructions.AgentName,
            Description = CoachInstructions.AgentDescription,

            // Empty, not null. Null would apply HarnessAgent.DefaultInstructions.
            HarnessInstructions = HarnessInstructions,

            // Every optional capability off. The coach gets the tools the registry enabled and nothing
            // else; how many that is depends on the feature switches, so it is not stated here.
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = true,

            // The same bounded loop the baseline gets through the application timeout.
            MaximumIterationsPerRequest = options.MaxIterationsPerRequest,

            // One shared builder, so the two arms are never compared under different limits.
            ChatOptions = CoachChatOptionsFactory.Create(options, tools)
        };
    }
}
