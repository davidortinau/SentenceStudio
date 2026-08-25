namespace SentenceStudio.Contracts.AppOperation;

/// <summary>
/// Marks the app-operation wire namespace as present and under the wire-tolerance policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>This namespace is empty of contracts on purpose.</b> It exists so the policy covers it from
/// the beginning: the architecture test walks
/// <see cref="SentenceStudio.Contracts.Wire.WireContractNamespaces.ClientWireRoots"/>, this
/// namespace is in that list, and the first enum added here without a
/// <see cref="SentenceStudio.Contracts.Wire.WireEnumFallbackAttribute"/> fails the build's test
/// run rather than shipping an intolerant client.
/// </para>
/// <para>
/// The alternative — add the namespace to the policy when the first type arrives — is the version
/// of this that does not work. The first type always arrives in a change that is about the type,
/// reviewed by somebody thinking about the type, and the policy is what gets forgotten.
/// </para>
/// </remarks>
public static class AppOperationWireSurface
{
    /// <summary>
    /// The namespace this marker guards, for callers that would otherwise hard-code the string.
    /// </summary>
    public const string Namespace = "SentenceStudio.Contracts.AppOperation";
}
