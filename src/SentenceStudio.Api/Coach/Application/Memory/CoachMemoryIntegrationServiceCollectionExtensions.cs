using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Coach.Memory;

namespace SentenceStudio.Api.Coach.Application.Memory;

/// <summary>
/// Wires learner memory into the coach session lane.
/// </summary>
/// <remarks>
/// <para>
/// One entry point so the ordering constraint is enforced in code rather than remembered. The
/// memory registration deliberately uses <c>TryAddSingleton</c> for the change notifier so that
/// the session lane can supply a real one first; if this method registered in the other order the
/// no-op notifier would win and a forgotten preference would survive inside every live checkpoint.
/// That failure is silent, which is exactly why the order lives here and not in a comment in the
/// host.
/// </para>
/// <para>
/// Everything is registered regardless of the feature flag. The memory store, the selector, and
/// the session service all read <c>Coach:Memory:Enabled</c> themselves and return a disabled
/// outcome, so the flag stays a runtime toggle instead of becoming a startup-only decision that
/// turns into a resolution failure the first time an operator flips it.
/// </para>
/// </remarks>
public static class CoachMemoryIntegrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the checkpoint rotator, then the memory backend, then the turn coordinator.
    /// Call after the coach persistence stores are registered.
    /// </summary>
    public static IServiceCollection AddCoachMemoryIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // First. AddCoachMemory uses TryAddSingleton for this interface precisely so the session
        // lane can claim it, and claiming it is the whole mechanism by which forgetting reaches a
        // live conversation.
        services.TryAddSingleton<ICoachMemoryChangedNotifier, CoachMemoryCheckpointRotator>();

        services.AddCoachMemory(configuration);

        // The session lane's only door to memory. Scoped, because the store and selector under it
        // are request-scoped and resolve the owner from the authenticated scope.
        services.TryAddScoped<CoachMemoryTurnCoordinator>();

        return services;
    }
}
