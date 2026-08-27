using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// The closed set of services the restricted <c>HarnessAgent</c> may resolve.
/// </summary>
/// <remarks>
/// <para>
/// The harness agent takes an <see cref="IServiceProvider"/> so its internals can resolve the
/// pieces an agent run needs. Handing it the application root container would put every
/// registered service in reach of a model-driven pipeline: the coach and identity
/// <c>DbContext</c>s, the session store, the configuration root (which carries connection
/// strings and API keys), the data protection provider, and every HTTP client. None of those
/// are needed for a coach turn, and the harness has no allow-list of its own.
/// </para>
/// <para>
/// This builds a child collection that holds only the three dependencies a coach turn actually
/// needs, plus the two container-plumbing services a provider must answer for. Everything else
/// resolves to <see langword="null"/>. The descriptor list is public so a startup contract test
/// can assert the exact set, which makes an accidental addition a build-time failure instead of
/// a silent privilege escalation.
/// </para>
/// <para>
/// Every descriptor is an <em>instance</em> registration. That is deliberate: the container never
/// constructs anything here, so it never takes ownership of the lifetimes, and disposing the
/// restricted provider can never dispose the application's chat client or logger factory.
/// </para>
/// </remarks>
public static class CoachHarnessServices
{
    /// <summary>
    /// The exact service types the restricted provider answers.
    /// Ordered to match <see cref="Describe"/> so the contract test can compare sequences.
    /// </summary>
    public static IReadOnlyList<Type> AllowedServiceTypes { get; } =
    [
        typeof(IChatClient),
        typeof(ILoggerFactory),
        typeof(TimeProvider)
    ];

    /// <summary>
    /// Types that must never resolve from the restricted provider. Named explicitly so the
    /// contract test reads as a security statement rather than a reflection sweep, and so a
    /// future refactor that re-introduces the root container fails loudly.
    /// </summary>
    /// <remarks>
    /// Held as type names rather than types because most of them live in assemblies this file
    /// must not take a dependency on, and because the test asserts the <em>absence</em> of a
    /// registration — a name is enough for that and keeps the list readable.
    /// </remarks>
    public static IReadOnlyList<string> ForbiddenServiceTypeNames { get; } =
    [
        // Data access. The harness must never reach a database, in either direction.
        "CoachDbContext",
        "ApplicationDbContext",
        "DbContext",
        "DbContextOptions",
        "ICoachSessionStore",
        "ICoachUsageStore",
        "ICoachPlanRevisionStore",
        "ICoachDataDeletionService",
        // Hands out a live ApplicationDbContext bound to an open connection and transaction, so it
        // is a database reach in everything but name.
        "ICoachDeletionEnlistment",
        "IDbConnection",
        "DbConnection",

        // Configuration and secrets.
        "IConfiguration",
        "IConfigurationRoot",
        "IDataProtectionProvider",
        "ICoachAgentSessionProtector",

        // Identity and the caller's own scope.
        "UserManager`1",
        "SignInManager`1",
        "IHttpContextAccessor",
        "ClaimsPrincipal",

        // Network egress and shell-like reach.
        "IHttpClientFactory",
        "HttpClient",
        "Process",

        // Tools are passed as function instances on the chat options, never resolved.
        "ICoachToolFactory",
        "ICoachAgentFactory"
    ];

    /// <summary>
    /// Builds the restricted child collection. Callers normally want <see cref="Build"/>;
    /// this is exposed so the contract test can inspect descriptors without a provider.
    /// </summary>
    /// <param name="chatClient">The already-resolved chat client for this run.</param>
    /// <param name="modelLoggerFactory">
    /// The content-free factory. The application factory must never be passed here: the harness
    /// pipeline writes prompts, model output, and tool arguments once its categories reach
    /// Debug/Trace.
    /// </param>
    /// <param name="timeProvider">
    /// Time. Supplied so the harness cannot be the one thing in the coach that reads the wall
    /// clock directly, and so tests stay deterministic.
    /// </param>
    public static IServiceCollection Describe(
        IChatClient chatClient,
        ILoggerFactory modelLoggerFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(modelLoggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        IServiceCollection services = new ServiceCollection();

        // Instance registrations only. See the class remarks: this keeps lifetime ownership
        // with the application container.
        services.Add(ServiceDescriptor.Singleton(typeof(IChatClient), chatClient));
        services.Add(ServiceDescriptor.Singleton(typeof(ILoggerFactory), modelLoggerFactory));
        services.Add(ServiceDescriptor.Singleton(typeof(TimeProvider), timeProvider));

        return services;
    }

    /// <summary>Builds the restricted provider handed to the harness agent.</summary>
    public static CoachHarnessServiceProvider Build(
        IChatClient chatClient,
        ILoggerFactory modelLoggerFactory,
        TimeProvider timeProvider) =>
        new(Describe(chatClient, modelLoggerFactory, timeProvider));
}

/// <summary>
/// A deliberately small <see cref="IServiceProvider"/> over a frozen set of instance
/// registrations. Anything not registered resolves to <see langword="null"/>.
/// </summary>
/// <remarks>
/// A hand-written provider is used instead of <c>BuildServiceProvider()</c> so the reachable
/// surface is exactly the descriptor list and nothing the container adds for itself. It also
/// removes the disposal question entirely: this owns no instances, so it has nothing to dispose
/// and cannot tear down the application's chat client.
/// </remarks>
public sealed class CoachHarnessServiceProvider : IServiceProvider, IServiceProviderIsService
{
    private readonly Dictionary<Type, object> _instances;

    internal CoachHarnessServiceProvider(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _instances = new Dictionary<Type, object>(services.Count);
        foreach (var descriptor in services)
        {
            // The contract is instance-only; a factory or implementation-type descriptor would
            // mean the container constructs something, which is exactly what this avoids.
            if (descriptor.ImplementationInstance is null)
            {
                throw new InvalidOperationException(
                    $"The restricted coach harness container accepts instance registrations only. " +
                    $"'{descriptor.ServiceType.Name}' was registered another way.");
            }

            _instances[descriptor.ServiceType] = descriptor.ImplementationInstance;
        }

        Descriptors = [.. services];
    }

    /// <summary>The frozen registration set, for the startup contract test.</summary>
    public IReadOnlyList<ServiceDescriptor> Descriptors { get; }

    /// <summary>The service types this provider answers, including container plumbing.</summary>
    public IReadOnlyList<Type> ProvidedServiceTypes =>
    [
        .. _instances.Keys,
        typeof(IServiceProvider),
        typeof(IServiceProviderIsService)
    ];

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (_instances.TryGetValue(serviceType, out var instance))
        {
            return instance;
        }

        // Returning this provider — never the application root — keeps a nested resolution
        // inside the same restricted surface.
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceProviderIsService))
        {
            return this;
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return _instances.ContainsKey(serviceType)
            || serviceType == typeof(IServiceProvider)
            || serviceType == typeof(IServiceProviderIsService);
    }
}
