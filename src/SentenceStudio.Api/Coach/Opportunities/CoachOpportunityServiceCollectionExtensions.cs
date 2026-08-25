using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// Registers the Sam opportunity ledger.
/// </summary>
/// <remarks>
/// <para>
/// Registration is unconditional; <em>behaviour</em> is not. The recorder is always resolvable so
/// no call site has to null-check, and it no-ops when <c>Coach:Opportunities:Enabled</c> is
/// false. That keeps "capture is off" a configuration fact rather than a branch repeated at three
/// boundaries, and it means turning capture on needs no redeploy.
/// </para>
/// <para>
/// The deletion contributor is registered with <c>TryAddEnumerable</c> so the deletion
/// coordinator discovers it. It is registered here, next to the table it owns, rather than in the
/// deletion extension — the point of discovery is that the two lanes do not have to be edited
/// together.
/// </para>
/// </remarks>
public static class CoachOpportunityServiceCollectionExtensions
{
    /// <summary>Adds the opportunity ledger's options, recorder, detector, and retention sweep.</summary>
    public static IServiceCollection AddCoachOpportunities(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<CoachOpportunityOptions>()
                .Bind(configuration.GetSection(CoachOpportunityOptions.SectionName))
                .ValidateOnStart();

        // Environment-aware, so "the operator surface is enabled outside Development" is a
        // startup failure rather than a screen somebody discovers in production.
        //
        // Registered as an instance rather than a factory, matching CoachOptionsValidator: a
        // factory delegate's implementation type is the service type itself, which makes it
        // indistinguishable to TryAddEnumerable and throws at registration.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CoachOpportunityOptions>>(
            new CoachOpportunityOptionsValidator(environment)));

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<ICoachOpportunityRecorder, CoachOpportunityRecorder>();
        services.TryAddScoped<CoachUnboundAnswerDetector>();
        services.TryAddScoped<CoachOpportunityRetentionSweep>();

        // The read/review surface. Registered only in Development, so a host in any other
        // environment cannot resolve it even if a route registration were reintroduced by
        // mistake — one more independent way for the surface to be absent rather than merely
        // switched off.
        if (environment.IsDevelopment())
        {
            services.TryAddScoped<CoachOpportunityOperatorService>();
        }

        // Discovered by CoachDataDeletionService, which holds no hand-maintained table list.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICoachDataDeletionContributor, CoachOpportunityDeletionContributor>());

        return services;
    }
}
