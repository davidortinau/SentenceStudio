using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Application;

using SentenceStudio.Api.Coach.Application.Compatibility;
using SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// Registers the baseline Learning Coach: the agent arm, the application reducer, and the
/// process-local run/idempotency state.
/// </summary>
public static class CoachApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Wires the baseline coach. Call after <c>AddCoachRuntime</c>, <c>AddCoachPersistence</c>,
    /// and <c>AddCoachReadOnlyTools</c>.
    /// </summary>
    /// <remarks>
    /// Nothing here resolves an <c>IChatClient</c> at registration time. The agent factory
    /// resolves one lazily, so a host with no AI configuration still starts, still answers
    /// <c>/availability</c>, and returns 503 only when a turn actually needs the model.
    /// </remarks>
    public static IServiceCollection AddCoachBaseline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Fail startup rather than silently running the baseline when an operator selects an
        // arm that does not exist yet.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CoachOptions>, CoachImplementationAvailabilityValidator>());

        // Stateless: holds configuration and the root provider only, never a learner or a scope.
        services.TryAddSingleton<ICoachAgentFactory, CoachAgentFactory>();

        // Process-local, like the Stage 1 budget service. Both must move to a shared store
        // before the coach runs on more than one instance.
        services.TryAddSingleton<CoachRunRegistry>();
        services.TryAddSingleton<CoachTurnIdempotencyStore>();

        // Safety validators. The allow-list is injected into the agent factory, the leak
        // validator and its server-only data source into the reducer, so every one of them
        // has an active call site rather than a registration only.
        services.TryAddSingleton<CoachToolAllowList>();
        services.TryAddSingleton<CoachIntentValidator>();
        services.TryAddScoped<CoachDueItemLeakValidator>();
        services.TryAddScoped<ICoachValidationDataSource, CoachValidationDataSource>();

        // Tenant-scoped: the resolver reads the trusted user scope itself, so nothing here — and
        // no model output — can address another learner's vocabulary.
        services.TryAddScoped<IVocabularyFocusResolver, VocabularyFocusResolver>();
        services.TryAddScoped<CoachVocabularyFocusService>();

        services.TryAddSingleton<CoachConstraintMapper>();
        services.TryAddSingleton<CoachExplicitAcceptanceClassifier>();

        // W8. Registered beside its sibling: both are stateless deterministic classifiers over
        // typed learner text, and a reviewer looking for one should find the other.
        services.TryAddSingleton<CoachCorrectionClassifier>();
        services.TryAddScoped<CoachDisputeCoordinator>();
        services.TryAddSingleton<CoachSuggestionValidator>();
        services.TryAddSingleton<CoachAnswerProjection>();
        services.TryAddSingleton<CoachWriteAuthority>();

        // Scoped: reads the current learner's profile to resolve their language tags.
        services.TryAddScoped<ICoachLanguageResolver, CoachLanguageResolver>();

        // Scoped: depends on the request-scoped tools and plan services.
        services.TryAddScoped<CoachPlanProjection>();

        // Both arms are registered, but only the configured one is resolved for a turn.
        // Selecting inside the factory keeps one interface, one reducer, and one validation
        // path: the flag chooses a pipeline, never a second behavior.
        services.TryAddScoped<BaselineLearningCoach>();
        services.TryAddScoped<HarnessLearningCoach>();
        services.TryAddScoped<ILearningCoach>(static provider =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<CoachOptions>>().CurrentValue;
            return options.Implementation == CoachImplementation.Harness
                ? provider.GetRequiredService<HarnessLearningCoach>()
                : provider.GetRequiredService<BaselineLearningCoach>();
        });

        services.TryAddScoped<ICoachSessionService, CoachSessionService>();

        // Durable history. Registered unconditionally: the service reads the DurableHistory flag
        // itself and gates every method on it. Registering conditionally would make the flag a
        // startup-only decision and turn a runtime toggle into a resolution failure.
        //
        // Singleton, and deliberately not scoped: it renews a lease from a timer while the
        // request that owns it is still using its own database context, so it must resolve a
        // context of its own rather than borrow one that is mid-query.
        services.TryAddSingleton<ICoachTurnLeaseRenewer, ScopedCoachTurnLeaseRenewer>();
        services.TryAddScoped<ICoachConversationService, CoachConversationService>();

        // The compatibility fork for the old /sessions routes. Registered as its own concrete
        // type rather than as ICoachSessionService: the conversation service depends on the
        // session service, so decorating that interface would send the durable path's own inner
        // calls back through this fork and straight into itself.
        services.TryAddScoped<CoachCompatibilitySessionService>();

        return services;
    }

    /// <summary>
    /// True when the configured implementation is the harness arm.
    /// </summary>
    /// <remarks>
    /// The arm now exists, so this reports the selection instead of blocking it. It stays
    /// public because the availability validator and the tests both name the same rule.
    /// </remarks>
    public static bool RequiresHarnessArm(CoachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Implementation == CoachImplementation.Harness;
    }
}

/// <summary>
/// Rejects an implementation value that is not a defined arm. Both <c>baseline</c> and
/// <c>harness</c> are available; an out-of-range value must stop the host rather than fall
/// back to the baseline, because a silent fallback would make an A/B comparison measure the
/// same arm twice.
/// </summary>
public sealed class CoachImplementationAvailabilityValidator : IValidateOptions<CoachOptions>
{
    public ValidateOptionsResult Validate(string? name, CoachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Enum.IsDefined(options.Implementation)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{CoachOptions.SectionName}:Implementation '{(int)options.Implementation}' is not a coach arm. Use 'baseline' or 'harness'.");
    }
}
