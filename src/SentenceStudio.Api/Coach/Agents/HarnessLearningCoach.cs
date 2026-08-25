using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// The harness arm. Selected only by <c>Coach:Implementation=harness</c>.
/// </summary>
/// <remarks>
/// <para>
/// This arm shares the instructions, the registered tool set, the typed
/// <c>CoachTurnIntent</c> structured output, the <c>AgentSession</c> serialization, the
/// keyed fast-tier chat client, the timeout and cancel mapping, and the whole application
/// validation path with <see cref="BaselineLearningCoach"/>. Both call
/// <see cref="CoachAgentTurnRunner"/>, so the only measured difference is the agent pipeline.
/// </para>
/// <para>
/// The agent is built per run from the calling scope's tools. A <c>HarnessAgent</c> is a
/// delegating wrapper around a <c>ChatClientAgent</c>, so construction allocates a short
/// pipeline of decorators; that cost is small next to one model round trip, and it is the
/// price of never letting a long-lived agent capture a scoped <c>DbContext</c>, a user scope,
/// or another learner's tool instances.
/// </para>
/// </remarks>
public sealed class HarnessLearningCoach : ILearningCoach
{
    private readonly ICoachAgentFactory _agentFactory;
    private readonly ICoachToolFactory _toolFactory;
    private readonly IOptionsMonitor<CoachOptions> _options;
    private readonly CoachTelemetry _telemetry;
    private readonly ILogger<HarnessLearningCoach> _logger;
    private readonly Tools.Observation.ICoachTurnObservationSink? _observationSink;

    public HarnessLearningCoach(
        ICoachAgentFactory agentFactory,
        ICoachToolFactory toolFactory,
        IOptionsMonitor<CoachOptions> options,
        CoachTelemetry telemetry,
        ILogger<HarnessLearningCoach> logger,
        Tools.Observation.ICoachTurnObservationSink? observationSink = null)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _toolFactory = toolFactory ?? throw new ArgumentNullException(nameof(toolFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Optional: a host without the observation seam registered still runs a turn.
        _observationSink = observationSink;
    }

    public CoachImplementation Implementation => CoachImplementation.Harness;

    public async Task<CoachAgentTurnResult> RunTurnAsync(
        CoachAgentTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Tools are resolved per request from the calling scope, so each one sees the
        // current learner through IUserScopeProvider and nothing is cached across users.
        // The budget is created here and shared by this turn's tools only, so one turn's
        // spending cannot reduce the next turn's allowance.
        var (tools, toolBudget) = CoachToolCallBudget.Apply(_toolFactory.CreateTools());

        Microsoft.Agents.AI.AIAgent? agent;
        try
        {
            // The factory runs the tool allow-list. A rejected set is a terminal failure:
            // the run stops here and no model call is made.
            agent = _agentFactory.TryCreateHarnessAgent(tools);
        }
        catch (CoachContractViolationException ex)
        {
            _logger.LogError(
                "[Coach] Session {SessionId}: the {Contract} rejected the tool set with {ViolationCount} violation(s); no model call was made.",
                request.SessionId, ex.Contract, ex.Violations.Count);
            return CoachAgentTurnResult.Failure(
                CoachAgentOutcome.Failed, "The tool set failed its safety contract.");
        }

        if (agent is null)
        {
            return CoachAgentTurnResult.Failure(
                CoachAgentOutcome.ModelUnavailable, "No chat client is configured on this host.");
        }

        var result = await CoachAgentTurnRunner
            .RunAsync(agent, request, _options.CurrentValue, Implementation, _telemetry, _logger, cancellationToken)
            .ConfigureAwait(false);

        // Logged rather than measured: the approved metric dimensions are a closed set, and a
        // turn's tool spend is a debugging detail rather than a dashboard series.

        // ── Amendment A1: the one W6 write into the W4 observation seam ──────
        //
        // W4 left BudgetUsed and BudgetLimit nullable and unset, because the budget object lives
        // out here in the agent arms and the no-leak boundary put these files off-limits to that
        // workstream. W6 is the workstream that needs them: a trace showing three calls against a
        // limit of three is a turn that stopped because it ran out, not a turn that decided it had
        // enough, and an honesty rule reads those two cases very differently.
        //
        // Recorded once, at the turn boundary, from the real budget. Explicitly NOT a synthetic
        // tool observation: a fake entry in Calls would inflate every count the trace reports and
        // break the seam's guarantee that ordinals are 1-based and contiguous over real calls.
        _observationSink?.RecordBudget(toolBudget.Used, toolBudget.Limit);

        _logger.LogDebug(
            "[Coach] Session {SessionId}: the turn used {UsedCalls} of {ToolCallLimit} tool calls.",
            request.SessionId, toolBudget.Used, toolBudget.Limit);

        return result;
    }
}
