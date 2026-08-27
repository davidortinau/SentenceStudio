using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Application.Learners;
using SentenceStudio.Application.Practice;
using SentenceStudio.Application.Resources;
using SentenceStudio.Application.Skills;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Data;

namespace SentenceStudio.Application;

/// <summary>
/// Registers the typed read contracts that sit between a caller and the tables.
/// </summary>
/// <remarks>
/// <para>
/// Four of the five contracts resolve to a repository the host has already registered, because
/// those repositories own their tables and every screen already reads through them. Binding the
/// interface to the same instance is what makes "the agent and the screen see the same rows" a
/// fact about the object graph rather than a convention two code paths happen to share.
/// </para>
/// <para>
/// The host decides those repositories' lifetimes; this method only adds the alias, so a caller
/// that resolves <see cref="ILearningResourceQueries"/> and a caller that resolves
/// <see cref="LearningResourceRepository"/> get the same object.
/// </para>
/// </remarks>
public static class ApplicationQueryServiceCollectionExtensions
{
    /// <summary>
    /// Adds the read contracts. The host must already have registered
    /// <see cref="UserProfileRepository"/>, <see cref="SkillProfileRepository"/>,
    /// <see cref="LearningResourceRepository"/>, and <see cref="VocabularyProgressRepository"/>.
    /// </summary>
    public static IServiceCollection AddApplicationQueries(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILearnerProfileQueries>(
            sp => sp.GetRequiredService<UserProfileRepository>());
        services.TryAddSingleton<ISkillProfileQueries>(
            sp => sp.GetRequiredService<SkillProfileRepository>());
        services.TryAddSingleton<ILearningResourceQueries>(
            sp => sp.GetRequiredService<LearningResourceRepository>());
        services.TryAddSingleton<IVocabularyQueries>(
            sp => sp.GetRequiredService<VocabularyProgressRepository>());

        // The one contract with no existing owner: DailyPlan, DailyPlanCompletion, and
        // UserActivity had no repository a multi-tenant host could call. Singleton for the same
        // reason as the repositories above — it opens a scope per query rather than holding a
        // context.
        services.TryAddSingleton<IPracticeHistoryQueries, PracticeHistoryQueries>();

        return services;
    }
}
