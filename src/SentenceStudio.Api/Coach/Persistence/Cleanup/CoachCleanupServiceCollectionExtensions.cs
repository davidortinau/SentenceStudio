using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Coach.Persistence.Cleanup;

/// <summary>
/// Registers the scheduled coach retention job.
/// </summary>
public static class CoachCleanupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cleanup runner, the expired-session filter default, the lease, and — when
    /// the environment allows it — the hosted scheduler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scheduler is withheld in Testing. A test host that silently deletes rows on a timer
    /// produces failures that reproduce only sometimes and only under load, and the job under
    /// test is exercised directly through <see cref="CoachCleanupRunner"/> anyway.
    /// </para>
    /// <para>
    /// The lease implementation is chosen from the provider, not from configuration: an operator
    /// cannot accidentally select the single-process lease on a multi-replica PostgreSQL
    /// deployment, which is the one mistake here that silently corrupts nothing and deadlocks
    /// everything.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCoachCleanupScheduling(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<CoachCleanupOptions>()
                .Bind(configuration.GetSection(CoachCleanupOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICoachExpiredSessionFilter, CheckpointOnlyExpiredSessionFilter>();
        services.TryAddScoped<CoachCleanupRunner>();

        services.TryAddScoped<ICoachCleanupLease>(static provider =>
        {
            var db = provider.GetRequiredService<CoachDbContext>();

            return db.Database.IsNpgsql()
                ? new PostgresCoachCleanupLease(
                    db,
                    provider.GetRequiredService<ILogger<PostgresCoachCleanupLease>>())
                : new InProcessCoachCleanupLease();
        });

        if (environment.IsEnvironment("Testing"))
        {
            return services;
        }

        var coachEnabled = configuration.GetValue($"{CoachOptions.SectionName}:Enabled", true);

        if (coachEnabled)
        {
            services.AddHostedService<CoachCleanupBackgroundService>();
        }

        return services;
    }
}
