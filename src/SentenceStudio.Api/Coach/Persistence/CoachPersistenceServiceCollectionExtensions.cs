using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Application.Memory;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// Registration helpers for coach persistence.
/// </summary>
/// <remarks>
/// <b>Not yet called from <c>Program.cs</c>.</b> Wiring is a separate, single-owner change
/// so this work never edits the shared startup file. When the coach endpoints land, the
/// host calls <see cref="AddCoachPersistence"/> after the Npgsql resource is available.
/// </remarks>
public static class CoachPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the coach stores, the agent-session protector, and the cleanup service.
    /// The caller registers <see cref="CoachDbContext"/> itself (through Aspire's
    /// <c>AddNpgsqlDbContext</c>) so connection-string resolution stays in one place.
    /// </summary>
    /// <param name="configuration">
    /// Accepted for call-site symmetry with the other coach registrations. Persistence reads
    /// <b>no</b> configuration section of its own: retention, expiry, and the agent config
    /// version all come from <see cref="CoachOptions"/> (<c>Coach:*</c>, bound by
    /// <c>AddCoachRuntime</c>) through <see cref="CoachPersistenceOptionsSetup"/>. Adding a
    /// <c>Coach:Persistence:*</c> section here would give an operator two keys for one knob.
    /// </param>
    public static IServiceCollection AddCoachPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CoachPersistenceOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<CoachPersistenceOptions>, CoachPersistenceOptionsSetup>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICoachAgentSessionProtector, DataProtectionCoachAgentSessionProtector>();
        services.TryAddScoped<ICoachSessionStore, CoachSessionStore>();
        services.TryAddScoped<ICoachUsageStore, CoachUsageStore>();
        services.TryAddScoped<CoachExpiryCleanupService>();

        AddCoachHistory(services);

        // Learner memory. Called from here rather than from the host because the memory store
        // resolves the coach DbContext and the content protector registered immediately above,
        // so the ordering requirement is satisfied structurally instead of by convention.
        services.AddCoachMemoryIntegration(configuration);

        return services;
    }

    /// <summary>
    /// Registers durable Sam conversation history: the content protector, the three owner-scoped
    /// stores, the export reader, and the deletion contributor.
    /// </summary>
    /// <remarks>
    /// The deletion contributor is registered through <c>TryAddEnumerable</c> so the deletion
    /// coordinator discovers it by resolving <c>IEnumerable&lt;ICoachDataDeletionContributor&gt;</c>.
    /// That keeps the coordinator free of a hand-maintained table list, which is the list that
    /// goes stale the first time a new store lands in a different lane.
    /// </remarks>
    private static void AddCoachHistory(IServiceCollection services)
    {
        services.TryAddScoped<History.ICoachContentProtector, History.DataProtectionCoachContentProtector>();
        services.TryAddScoped<History.ICoachConversationStore, History.CoachConversationStore>();
        services.TryAddScoped<History.ICoachMessageStore, History.CoachMessageStore>();
        services.TryAddScoped<History.ICoachTurnOperationStore, History.CoachTurnOperationStore>();
        services.TryAddScoped<History.ICoachHistoryExportReader, History.CoachHistoryExportReader>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<History.ICoachDataDeletionContributor, History.CoachHistoryDeletionContributor>());
    }

    /// <summary>
    /// Applies pending coach migrations. Kept separate from <see cref="AddCoachPersistence"/>
    /// so the host decides when (and whether) migration runs at startup.
    /// </summary>
    public static async Task MigrateCoachDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
