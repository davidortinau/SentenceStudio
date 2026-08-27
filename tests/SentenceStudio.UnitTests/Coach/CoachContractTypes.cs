using System.Reflection;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// Shared reflection helpers for the coach contract tests.
/// </summary>
internal static class CoachContractTypes
{
    private const string RootNamespace = "SentenceStudio.Contracts.Coach";

    /// <summary>Every public type in the coach contract namespaces.</summary>
    public static IReadOnlyList<Type> All { get; } = typeof(CoachTurnResponse).Assembly
        .GetTypes()
        .Where(t => t.IsPublic && t.Namespace is not null && t.Namespace.StartsWith(RootNamespace, StringComparison.Ordinal))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>The public data shapes: classes with properties.</summary>
    public static IReadOnlyList<Type> DataShapes { get; } = All
        .Where(t => t is { IsClass: true, IsAbstract: false })
        .ToList();

    /// <summary>The public closed enums.</summary>
    public static IReadOnlyList<Type> Enums { get; } = All.Where(t => t.IsEnum).ToList();

    /// <summary>The model-facing intent shapes.</summary>
    public static IReadOnlyList<Type> IntentShapes { get; } = DataShapes
        .Where(t => t.Namespace == typeof(CoachTurnIntent).Namespace)
        .ToList();

    /// <summary>
    /// The shapes the API sends to a client and the model never sees.
    /// </summary>
    /// <remarks>
    /// Everything that is not an intent shape. These are judged under the bounded public rules
    /// rather than the strict model-facing ones, because a history contract carrying the learner's
    /// own conversation back to them is the feature and not a leak.
    /// </remarks>
    public static IReadOnlyList<Type> PublicClientShapes { get; } = DataShapes
        .Where(t => t.Namespace != typeof(CoachTurnIntent).Namespace)
        .ToList();

    public static IEnumerable<PropertyInfo> PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
}
