using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// Builds the coach <see cref="AIAgent"/>. Registered as a <b>singleton</b> and holds no
/// learner state: every call takes the per-request tool instances, so nothing scoped is
/// captured across requests.
/// </summary>
/// <remarks>
/// Both create methods run the tool set through <see cref="CoachToolAllowList"/> first. That
/// check is the last gate before an agent can see a tool, so a set that is missing a tool,
/// carries an extra one, names a change action, accepts an identity argument, or leaves its
/// argument shape open stops the run here — before any model call.
/// </remarks>
public interface ICoachAgentFactory
{
    /// <summary>
    /// True when a chat client is configured on this host. Availability and feature-off
    /// paths call this instead of resolving a client, so a host with no AI configuration
    /// still boots and still answers <c>/availability</c>.
    /// </summary>
    bool IsModelAvailable { get; }

    /// <summary>
    /// Creates a fresh agent over the supplied per-request tools.
    /// Returns null when no chat client is configured.
    /// </summary>
    AIAgent? TryCreateAgent(IReadOnlyList<AIFunction> tools);

    /// <summary>
    /// Creates a fresh restricted <c>HarnessAgent</c> over the supplied per-request tools.
    /// Returns null when no chat client is configured.
    /// </summary>
    /// <remarks>
    /// Built per run, like the baseline agent, so no long-lived agent captures a scoped
    /// <c>DbContext</c>, a user scope, or another learner's tool instances.
    /// </remarks>
    AIAgent? TryCreateHarnessAgent(IReadOnlyList<AIFunction> tools);
}

/// <inheritdoc cref="ICoachAgentFactory"/>
/// <remarks>
/// Both create paths hand the agent internals <see cref="CoachModelLoggerFactory.Safe"/> instead of
/// the application <see cref="ILoggerFactory"/>, so raising Agent Framework or
/// Microsoft.Extensions.AI categories to Debug/Trace cannot turn prompts, model responses, or tool
/// arguments into log output. The application factory is still used for this class's own
/// shape-only logs.
/// </remarks>
public sealed class CoachAgentFactory : ICoachAgentFactory
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<CoachOptions> _options;
    private readonly ILogger<CoachAgentFactory> _logger;
    private readonly ILoggerFactory _modelLoggerFactory;
    private readonly CoachToolAllowList _allowList;
    private readonly TimeProvider _timeProvider;

    /// <summary>Guards the cached restricted container. The factory is a singleton.</summary>
    private readonly Lock _harnessServicesGate = new();
    private IChatClient? _harnessServicesClient;
    private CoachHarnessServiceProvider? _harnessServices;

    /// <param name="loggerFactory">
    /// The application factory. Used only for this class's own shape-only logs; it is never handed
    /// to the agent internals.
    /// </param>
    /// <param name="modelLoggerFactory">
    /// The seam for the factory given to Agent Framework and Microsoft.Extensions.AI internals.
    /// Defaults to <see cref="CoachModelLoggerFactory.Safe"/>. Nothing registers this type, so DI
    /// always leaves it null and the safe default applies.
    /// </param>
    /// <param name="timeProvider">
    /// Time handed to the restricted harness container. Defaults to the system clock.
    /// </param>
    public CoachAgentFactory(
        IServiceProvider services,
        IOptionsMonitor<CoachOptions> options,
        ILoggerFactory loggerFactory,
        CoachToolAllowList? allowList = null,
        CoachModelLoggerFactory? modelLoggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<CoachAgentFactory>();
        _modelLoggerFactory = (modelLoggerFactory ?? CoachModelLoggerFactory.Safe).LoggerFactory;
        _allowList = allowList ?? new CoachToolAllowList();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The factory handed to the agent internals. Exposed so a test can prove it is not the
    /// application factory.
    /// </summary>
    public ILoggerFactory ModelLoggerFactory => _modelLoggerFactory;

    /// <summary>
    /// The restricted container handed to the harness agent, or null before the first harness
    /// build. Exposed so a test can prove the agent did not receive the application root
    /// provider and can assert the exact registration set.
    /// </summary>
    public CoachHarnessServiceProvider? RestrictedHarnessServices
    {
        get
        {
            lock (_harnessServicesGate)
            {
                return _harnessServices;
            }
        }
    }


    public bool IsModelAvailable => ResolveChatClient() is not null;

    public AIAgent? TryCreateAgent(IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var chatClient = ResolveChatClient();
        if (chatClient is null)
        {
            return null;
        }

        // Last gate before the tools are attached to an agent.
        RequireApprovedToolSet(tools);

        var options = _options.CurrentValue;

        // One bounded turn of constraint mapping. The fast tier is the right default:
        // the work is short, schema-constrained classification and extraction, not
        // multi-step reasoning, and the request runs inside a 45s learner-facing budget.
        // Move to the reasoning tier only if the trajectory evaluation shows the fast
        // tier mis-classifies acceptance or intent — cost and latency both roughly
        // double, and acceptance is authorised deterministically anyway.
        var agentOptions = new ChatClientAgentOptions
        {
            Name = CoachInstructions.AgentName,
            Description = CoachInstructions.AgentDescription,
            ChatOptions = CoachChatOptionsFactory.Create(options, tools)
        };

        // The agent internals get the content-free factory, never the application factory:
        // ChatClientAgent and the Microsoft.Extensions.AI pipeline log prompts, responses, and
        // tool arguments once their categories reach Debug/Trace.
        var agent = chatClient.AsAIAgent(agentOptions, _modelLoggerFactory);

        LogAgentCreated(CoachImplementation.Baseline, tools.Count);

        return agent;
    }

    public AIAgent? TryCreateHarnessAgent(IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var chatClient = ResolveChatClient();
        if (chatClient is null)
        {
            return null;
        }

        // Last gate before the tools are attached to an agent.
        RequireApprovedToolSet(tools);

        // Same client, same instructions, same tools, same token cap as the baseline arm;
        // only the surrounding pipeline differs. The harness options factory owns the
        // restriction policy so the flags cannot drift between here and the tests.
        var harnessOptions = CoachHarnessOptionsFactory.Create(_options.CurrentValue, tools);

        // Never the root container. The harness resolves from a closed child container that
        // holds only the chat client, the content-free logger factory, and time; everything
        // else — DbContexts, configuration, identity, HTTP clients — resolves to null. See
        // CoachHarnessServices for the contract and CoachHarnessServiceProviderContractTests
        // for the test that fails when the set changes.
        //
        // The logger factory is the content-free one for the same reason as the baseline arm:
        // the harness pipeline logs prompts, model output, and tool arguments at Debug/Trace.
        var restrictedServices = ResolveHarnessServices(chatClient);

        var agent = new HarnessAgent(chatClient, harnessOptions, _modelLoggerFactory, restrictedServices);

        LogAgentCreated(CoachImplementation.Harness, tools.Count);

        return agent;
    }

    /// <summary>
    /// Returns the restricted container for this chat client, building it once.
    /// </summary>
    /// <remarks>
    /// Cached because the factory is a singleton and the container is immutable and learner-free:
    /// building one per turn would allocate on every request for no isolation benefit. It is keyed
    /// on the client instance so a re-registered or swapped client is never served a stale one.
    /// </remarks>
    private CoachHarnessServiceProvider ResolveHarnessServices(IChatClient chatClient)
    {
        lock (_harnessServicesGate)
        {
            if (_harnessServices is null || !ReferenceEquals(_harnessServicesClient, chatClient))
            {
                _harnessServices = CoachHarnessServices.Build(chatClient, _modelLoggerFactory, _timeProvider);
                _harnessServicesClient = chatClient;
            }

            return _harnessServices;
        }
    }

    /// <summary>
    /// Shape-only application log: which arm was built and how many tools it received.
    /// Carries no learner text, no prompt, no tool arguments, and no identifier.
    /// </summary>
    private void LogAgentCreated(CoachImplementation implementation, int toolCount) =>
        _logger.LogDebug(
            "Coach agent created. Arm={CoachImplementation} ToolCount={CoachToolCount}",
            implementation,
            toolCount);

    /// <summary>
    /// Fails the run when the tool set is not exactly the approved read-only set.
    /// Runs before the chat client is resolved, so a rejected set never reaches the model.
    /// </summary>
    private void RequireApprovedToolSet(IReadOnlyList<AIFunction> tools)
    {
        var result = _allowList.Validate(tools);
        if (!result.IsValid)
        {
            throw new CoachContractViolationException("coach tool allow-list", result);
        }
    }

    /// <summary>
    /// Resolves the keyed fast-tier client, falling back to the default registration.
    /// Both are singletons, so this is safe to call from a singleton factory.
    /// </summary>
    private IChatClient? ResolveChatClient() =>
        _services.GetKeyedService<IChatClient>(AiTier.Fast.ToKey())
        ?? _services.GetService<IChatClient>();
}
