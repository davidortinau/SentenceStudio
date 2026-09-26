namespace SentenceStudio.Contracts.AppOperation;

/// <summary>
/// Marks the app-operation wire namespace as present and under the wire-tolerance policy.
/// </summary>
/// <remarks>
/// <para>
/// The architecture test walks
/// <see cref="SentenceStudio.Contracts.Wire.WireContractNamespaces.ClientWireRoots"/>, this
/// namespace is in that list, and the first enum added here without a
/// <see cref="SentenceStudio.Contracts.Wire.WireEnumFallbackAttribute"/> fails the build's test
/// run rather than shipping an intolerant client.
/// </para>
/// </remarks>
public static class AppOperationWireSurface
{
    /// <summary>
    /// The namespace this marker guards, for callers that would otherwise hard-code the string.
    /// </summary>
    public const string Namespace = "SentenceStudio.Contracts.AppOperation";
}
