using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Registration for the Learning Coach runtime foundations: options, availability policy, budgets,
/// and telemetry.
/// </summary>
/// <remarks>
/// This registers only the always-safe pieces. It does not register an agent, a tool, a database
/// context, or an endpoint, and it does not read <c>Coach:Enabled</c> — the feature flag is
/// evaluated per request by <see cref="ICoachAvailabilityPolicy"/> so an operator can flip the kill
/// switch through configuration without a restart.
/// </remarks>
public static class CoachRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Adds validated <see cref="CoachOptions"/>, <see cref="ICoachAvailabilityPolicy"/>,
    /// <see cref="ICoachBudgetService"/>, and <see cref="CoachTelemetry"/>.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">The configuration the <c>Coach</c> section is bound from.</param>
    /// <param name="environment">
    /// The host environment. Supplied by the API host. It decides one thing only: whether the
    /// development cohort sentinel is permitted. Omitting it is treated as non-Development — the
    /// strict answer — so a caller that does not care about the sentinel need not supply one.
    /// </param>
    public static IServiceCollection AddCoachRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CoachOptions>()
            .Bind(configuration.GetSection(CoachOptions.SectionName))
            .ValidateOnStart();

        // The environment is resolved here, at registration, rather than injected by DI. Options
        // validators run during IOptions resolution, and reaching into the container at that
        // point would tie coach startup validation to a service a bare ServiceCollection (used by
        // several tests, and by any future non-web host) does not register. Registering concrete
        // instances also keeps TryAddEnumerable able to tell these two validators apart, which a
        // factory descriptor cannot.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CoachOptions>>(
            new CoachOptionsValidator(environment)));

        // Reads raw configuration, not bound options, because the failure it catches is a key
        // that binds to nothing. Built from the configuration this method was handed rather than
        // resolved from the container: callers already supply it, and demanding a second copy
        // from DI would break every caller that registers the coach runtime against a bare
        // service collection.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CoachOptions>>(
            new CoachConfigurationKeyValidator(configuration)));

        services.TryAddSingleton(TimeProvider.System);

        // The same environment instance the validator got, so startup validation and the runtime
        // cohort decision can never disagree about which host this is.
        services.TryAddSingleton<ICoachAvailabilityPolicy>(sp => new CoachAvailabilityPolicy(
            sp.GetRequiredService<IOptionsMonitor<CoachOptions>>(),
            environment));

        // Stage 1 only: process-local budgets. Replace with the PostgreSQL CoachUsage-backed
        // implementation before the coach runs on more than one instance.
        services.TryAddSingleton<ICoachBudgetService, InMemoryCoachBudgetService>();

        services.TryAddSingleton<CoachTelemetry>();

        return services;
    }
}
