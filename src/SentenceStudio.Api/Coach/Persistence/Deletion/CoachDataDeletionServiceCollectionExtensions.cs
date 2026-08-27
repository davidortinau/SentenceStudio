using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Coach.Persistence.Deletion;

/// <summary>
/// Registers the owner-scoped coach deletion coordinator and the checkpoint contributor.
/// </summary>
/// <remarks>
/// Kept in its own extension, and called from the host after <c>AddCoachPersistence</c>, so the
/// deletion lane owns its registrations instead of appending to another lane's file. The history
/// lane registers its own contributor the same way; the coordinator discovers both.
/// </remarks>
public static class CoachDataDeletionServiceCollectionExtensions
{
    /// <summary>Adds coach deletion. Safe to call more than once.</summary>
    public static IServiceCollection AddCoachDataDeletion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ICoachDataDeletionService, CoachDataDeletionService>();

        // Lets the coordinator put the application context on its own connection and transaction,
        // so an erasure that spans both contexts commits or rolls back as one. It reports itself
        // unavailable rather than throwing on a host where the two do not share a database, and the
        // coordinator only asks for it when a contributor actually writes through another context.
        services.TryAddScoped<ICoachDeletionEnlistment, SharedConnectionCoachDeletionEnlistment>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICoachDataDeletionContributor, CoachCheckpointDeletionContributor>());

        // The legacy conversation contributor is added only when the host has actually registered
        // the owner-scoped conversation service. Registering it unconditionally would make every
        // account deletion fail with a resolution error on a host that does not serve that
        // activity; resolving it as an optional null instead would let legacy rows survive an
        // erasure with nothing in the logs to say so. Presence of the descriptor is the honest
        // signal, and it is decidable here at startup.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IConversationOwnerDataService)))
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<ICoachDataDeletionContributor, LegacyConversationDeletionContributor>());
        }

        return services;
    }

    /// <summary>
    /// Registers the shared owner-scoped conversation service so account deletion can erase the
    /// learner's rows in the legacy conversation activity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service and its implementation both live in <c>SentenceStudio.Shared</c>, which the API
    /// already references; only the registration lived in the client app's composition root. This
    /// re-registers the same type against the API container rather than taking a dependency on
    /// <c>SentenceStudio.AppLib</c>, which would pull the whole client service graph — sync,
    /// preferences, UI concerns — into a web host that needs none of it.
    /// </para>
    /// <para>
    /// Singleton matches how the repository is built: it takes only <see cref="IServiceProvider"/>
    /// and creates its own scope per call to resolve <c>ApplicationDbContext</c>, so it holds no
    /// scoped state. Its optional client-side collaborators resolve to null here, which is the
    /// intended shape for a server host.
    /// </para>
    /// <para>
    /// Call this <b>before</b> <see cref="AddCoachDataDeletion"/> — that method decides whether to
    /// register the legacy contributor by looking for this descriptor.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddLegacyConversationOwnerData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ConversationRepository>();
        services.TryAddSingleton<IConversationOwnerDataService>(
            sp => sp.GetRequiredService<ConversationRepository>());

        return services;
    }
}
