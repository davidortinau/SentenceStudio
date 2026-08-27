using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// The plain-agent baseline arm. Builds a stable <c>ChatClientAgent</c> over the singleton
/// chat client and the tools the registry enabled, runs exactly one turn, and returns a typed
/// <see cref="CoachTurnIntent"/> produced by structured output.
/// </summary>
/// <remarks>
/// No harness, no memory provider, no file access, no skills, no web search, no todo list.
/// Conversation state lives only in the serialized <c>AgentSession</c>, which the
/// application encrypts before it reaches the database.
/// </remarks>
public sealed class BaselineLearningCoach : ILearningCoach
{
    private readonly ICoachAgentFactory _agentFactory;
    private readonly ICoachToolFactory _toolFactory;
    private readonly IOptionsMonitor<CoachOptions> _options;
    private readonly CoachTelemetry _telemetry;
    private readonly ILogger<BaselineLearningCoach> _logger;
    private readonly Tools.Observation.ICoachTurnObservationSink? _observationSink;

    public BaselineLearningCoach(
        ICoachAgentFactory agentFactory,
        ICoachToolFactory toolFactory,
        IOptionsMonitor<CoachOptions> options,
        CoachTelemetry telemetry,
        ILogger<BaselineLearningCoach> logger,
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

    public CoachImplementation Implementation => CoachImplementation.Baseline;

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
            agent = _agentFactory.TryCreateAgent(tools);
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

        // The turn body is shared with the harness arm so the two arms cannot drift.
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
