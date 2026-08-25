namespace SentenceStudio.Contracts.Wire;

/// <summary>
/// The namespaces whose public shapes cross the API/client boundary, and the ones that
/// deliberately do not.
/// </summary>
/// <remarks>
/// <para>
/// Shipped rather than kept in the test project, because "which contracts does a client parse" is
/// a product statement. A namespace added to the assembly and not added here is a surface nobody
/// decided to expose, and the architecture test says so.
/// </para>
/// </remarks>
public static class WireContractNamespaces
{
    /// <summary>Coach ("Sam") conversation, plan, write and report shapes.</summary>
    public const string Coach = "SentenceStudio.Contracts.Coach";

    /// <summary>
    /// The model-facing intent shapes. <b>Excluded from the client wire surface on purpose.</b>
    /// </summary>
    /// <remarks>
    /// These are what the server parses out of structured model output, and they must stay strict:
    /// a model that invents <c>"DeletePlan"</c> has to be refused, not read as "no change". No
    /// client DTO references one — the architecture test proves it — so making the client tolerant
    /// cannot loosen this boundary.
    /// </remarks>
    public const string CoachIntent = "SentenceStudio.Contracts.Coach.Intent";

    /// <summary>What the coach may remember about a learner, and the surface that manages it.</summary>
    public const string LearnerMemory = "SentenceStudio.Contracts.LearnerMemory";

    /// <summary>
    /// App-operation shapes. Empty today; in the policy from the start so the first type added
    /// arrives under the tolerance rules rather than being retrofitted into them.
    /// </summary>
    public const string AppOperation = "SentenceStudio.Contracts.AppOperation";

    /// <summary>Every namespace whose public enums must declare an unknown-value fallback.</summary>
    public static IReadOnlyList<string> ClientWireRoots { get; } =
    [
        Coach,
        LearnerMemory,
        AppOperation
    ];

    /// <summary>Namespaces excluded from the client wire surface, and kept strict.</summary>
    public static IReadOnlyList<string> ServerOnly { get; } = [CoachIntent];

    /// <summary>
    /// True when <paramref name="namespaceName"/> is a client wire namespace.
    /// </summary>
    /// <remarks>
    /// Matches a namespace or one of its descendants, minus anything under
    /// <see cref="ServerOnly"/>. Written this way so a new
    /// <c>SentenceStudio.Contracts.Coach.Something</c> namespace is covered the day it appears
    /// instead of the day somebody remembers to list it.
    /// </remarks>
    public static bool IsClientWire(string? namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return false;
        }

        if (ServerOnly.Any(excluded => Matches(namespaceName, excluded)))
        {
            return false;
        }

        return ClientWireRoots.Any(root => Matches(namespaceName, root));
    }

    private static bool Matches(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.Ordinal)
        || candidate.StartsWith(root + ".", StringComparison.Ordinal);
}
