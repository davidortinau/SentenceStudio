using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Reports;

/// <summary>
/// Registers the learner response-report surface.
/// </summary>
/// <remarks>
/// <para>
/// Registration is unconditional; <em>behaviour</em> is not. The service is always resolvable so
/// the routes can be mapped once and answer 404 while the feature is off, which keeps "reporting
/// is available here" a configuration fact that can be flipped without a redeploy rather than a
/// deployment-shaped decision.
/// </para>
/// <para>
/// The deletion contributor is registered with <c>TryAddEnumerable</c> so the deletion
/// coordinator discovers it, next to the table it owns rather than in the deletion extension —
/// the point of discovery is that the two lanes never have to be edited together.
/// </para>
/// </remarks>
public static class CoachResponseReportServiceCollectionExtensions
{
    /// <summary>Adds the report options, service, retention sweep, and deletion contributor.</summary>
    public static IServiceCollection AddCoachResponseReports(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CoachResponseReportOptions>()
                .Bind(configuration.GetSection(CoachResponseReportOptions.SectionName))
                .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CoachResponseReportOptions>>(
            new CoachResponseReportOptionsValidator()));

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<CoachResponseReportService>();
        services.TryAddScoped<CoachResponseReportRetentionSweep>();

        // Discovered by CoachDataDeletionService, which holds no hand-maintained table list.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICoachDataDeletionContributor, CoachResponseReportDeletionContributor>());

        return services;
    }
}

/// <summary>
/// Refuses to start a host whose report configuration leaves a decision unmade.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> an environment gate, unlike
/// <c>CoachOpportunityOptionsValidator</c>'s treatment of the operator surface. That surface can
/// decrypt a learner's messages and therefore has no business existing in production; this one
/// records a content-free row on an owner-scoped route that no model can reach, and those
/// properties hold identically in every environment. Making it Development-only would have been
/// theatre — and worse, it would have meant a control learners are offered could never reach
/// them.
/// </para>
/// <para>
/// What the validator does enforce is that a deployment accepting reports has also chosen how
/// long it keeps them. "On, and nobody decided the retention" is not a configuration, it is a
/// postponement, and it is the shape a data-retention finding takes a year later.
/// </para>
/// </remarks>
public sealed class CoachResponseReportOptionsValidator : IValidateOptions<CoachResponseReportOptions>
{
    /// <summary>The lowest retention window reports will accept.</summary>
    public const int MinRetentionDays = 7;

    /// <summary>The highest retention window reports will accept.</summary>
    public const int MaxRetentionDays = 730;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CoachResponseReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.RetentionDays is < MinRetentionDays or > MaxRetentionDays)
        {
            failures.Add(
                $"{CoachResponseReportOptions.SectionName}:RetentionDays must be between " +
                $"{MinRetentionDays} and {MaxRetentionDays}. It was {options.RetentionDays}.");
        }

        if (options.Enabled && !options.RetentionSweepEnabled)
        {
            failures.Add(
                $"'{CoachResponseReportOptions.SectionName}:Enabled' is true while " +
                "'RetentionSweepEnabled' is false. A deployment that accepts learner reports and " +
                "never ages them out has not chosen a retention policy, it has postponed one. " +
                "Enable the sweep, or turn reporting off.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
