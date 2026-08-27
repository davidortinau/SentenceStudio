using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// Registers the nine honesty rules, the engine, and the shadow router.
/// </summary>
/// <remarks>
/// <para>
/// The rules themselves are not registered individually and that is deliberate — the engine builds
/// its own set in its constructor. A rule resolved from DI is a rule a missing registration can
/// silently delete, and the resulting build would pass every test that does not happen to exercise
/// that one rule. Constructing the set in code means a dropped rule fails the census test instead.
/// </para>
/// <para>
/// The shadow router is registered separately because it is meant to be removable. Deleting D4 is
/// one line here and one file.
/// </para>
/// </remarks>
public static class CoachClaimRuleServiceCollectionExtensions
{
    /// <summary>Adds the claim rule engine and the shadow router.</summary>
    public static IServiceCollection AddCoachClaimRules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<CoachClaimRuleEngine>();

        // The per-turn record. Scoped for the same reason the tool observation buffer is: "the
        // turn" is the request scope, so there is no cross-turn state to leak or to clear. Without
        // it, Observe would be indistinguishable from Off — including to a test.
        services.TryAddScoped<ICoachClaimFindingBuffer, CoachClaimFindingBuffer>();

        // The one caller. Registered beside the engine rather than in the session service's own
        // registration so the engine and its call site cannot be wired independently — which is
        // exactly the state this workstream was rejected for.
        services.TryAddScoped<CoachTurnGroundingEvaluator>(provider => new CoachTurnGroundingEvaluator(
            provider.GetRequiredService<CoachClaimRuleEngine>(),
            provider.GetRequiredService<Capabilities.ICoachCapabilityResolver>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<CoachTurnGroundingEvaluator>(),
            provider.GetService<ICoachShadowClaimRouter>(),
            provider.GetService<ICoachClaimFindingBuffer>(),
            provider.GetService<CoachGroundingMetrics>()));

        // Singleton, because a Meter is expensive to build and its instruments are process-wide.
        // A scoped meter would create one instrument set per request, and the exporter would see a
        // new series for every turn.
        services.TryAddSingleton<CoachGroundingMetrics>();

        // Shadow only, per D4. Nothing reads its output except telemetry.
        services.TryAddScoped<ICoachShadowClaimRouter, CoachShadowClaimRouter>();

        return services;
    }
}
