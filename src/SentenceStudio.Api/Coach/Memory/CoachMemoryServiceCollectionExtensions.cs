using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// Registers learner memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not yet called from <c>Program.cs</c>.</b> Wiring is a separate, single-owner change so this
/// work never edits the shared startup file. The host calls <see cref="AddCoachMemory"/> after
/// <c>AddCoachPersistence</c>, because the store resolves <c>CoachDbContext</c> and
/// <c>ICoachContentProtector</c> from that registration.
/// </para>
/// <para>
/// Everything is registered whether or not the feature is switched on. The flag is enforced inside
/// the store and the selector, not by leaving services unregistered — a missing registration turns
/// a disabled feature into a startup crash the first time something resolves it.
/// </para>
/// </remarks>
public static class CoachMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the memory options, store, service, selector, notifier, and deletion contributor.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">
    /// Bound to <c>Coach:Memory</c>. The only key that matters operationally is
    /// <c>Coach:Memory:Enabled</c>, which defaults to <see langword="false"/>.
    /// </param>
    public static IServiceCollection AddCoachMemory(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Fails the host at startup rather than at the first request, and keeps the memory CRUD
        // surface on its own bounded contract instead of the model and tool output embargo.
        CoachMemoryContractValidator.EnsureValid();

        services.AddOptions<CoachMemoryOptions>()
                .Bind(configuration.GetSection(CoachMemoryOptions.SectionName))
                .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CoachMemoryOptions>, CoachMemoryOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);

        // The no-op notifier is a TryAdd, so the session lane can register a real one first and
        // this call will not overwrite it.
        services.TryAddSingleton<ICoachMemoryChangedNotifier, NoOpCoachMemoryChangedNotifier>();

        services.TryAddScoped<ICoachMemoryStore, CoachMemoryStore>();
        services.TryAddScoped<ICoachMemoryService, CoachMemoryService>();
        services.TryAddScoped<ICoachMemoryContextSelector, CoachMemoryContextSelector>();
        services.TryAddScoped<CoachMemorySourceDeletionHandler>();

        // Discovered by the deletion coordinator through IEnumerable<ICoachDataDeletionContributor>.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICoachDataDeletionContributor, CoachMemoryDeletionContributor>());

        return services;
    }
}
