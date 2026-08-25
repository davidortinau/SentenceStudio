using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Feedback.Persistence;

namespace SentenceStudio.Api.Feedback;

/// <summary>
/// Registers the feedback lane: its options, signing key, server-only context, ledger, limiter,
/// retention sweep, and erasure service.
/// </summary>
public static class FeedbackServiceCollectionExtensions
{
    /// <summary>
    /// Adds everything the feedback endpoints resolve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signing key is built <em>here</em>, at registration, rather than lazily on first
    /// request. A missing or unusable key in Production is a deployment defect, and the moment to
    /// report a deployment defect is at startup — where it fails the rollout — not on the first
    /// learner request, where it is a 500 in a feature nobody is watching. <c>ValidateOnStart</c>
    /// does the same job for the options.
    /// </para>
    /// <para>
    /// The context is registered with its own migrations history table and, deliberately, with no
    /// <c>PendingModelChangesWarning</c> suppression.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFeedback(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<FeedbackOptions>()
                .Bind(configuration.GetSection(FeedbackOptions.SectionName))
                .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FeedbackOptions>, FeedbackOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);

        // A generated key is a Development and Testing convenience only. Anywhere else, a missing
        // or shared key throws out of Create and the host never starts.
        var allowGeneratedKey = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        services.TryAddSingleton<IFeedbackHmacKeyProvider>(
            FeedbackHmacKeyProvider.Create(configuration, allowGeneratedKey));

        services.AddDbContext<FeedbackDbContext>(options =>
        {
            var connectionString =
                configuration.GetConnectionString("feedback")
                ?? configuration.GetConnectionString("sentencestudio");

            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(FeedbackSchema.MigrationsHistoryTable));
        });

        services.TryAddScoped<IFeedbackSubmissionLedger, FeedbackSubmissionLedger>();
        services.TryAddScoped<IFeedbackRateLimiter, FeedbackRateLimiter>();
        services.TryAddScoped<IFeedbackDataDeletionService, FeedbackDataDeletionService>();
        services.TryAddScoped<FeedbackRetentionSweep>();

        services.AddHostedService<FeedbackRetentionBackgroundService>();

        return services;
    }
}
