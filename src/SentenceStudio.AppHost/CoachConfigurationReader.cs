using Microsoft.Extensions.Configuration;

namespace SentenceStudio.AppHost;

/// <summary>
/// Result of reading the Coach allowlist. Carries the deduplicated list plus source indices
/// where duplicates were dropped, so the top-level AppHost can log a warning without exposing
/// the profile ID values.
/// </summary>
internal sealed class CoachAllowlistResult
{
    /// <summary>Deduplicated, compacted, trimmed entries in first-occurrence order.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>
    /// Source indices (0-based, from the configuration keys) where a duplicate was detected and
    /// dropped. Empty when no duplicates exist.
    /// </summary>
    public IReadOnlyList<int> DuplicateSourceIndices { get; }

    internal CoachAllowlistResult(IReadOnlyList<string> ids, IReadOnlyList<int> duplicateSourceIndices)
    {
        Ids = ids;
        DuplicateSourceIndices = duplicateSourceIndices;
    }
}

/// <summary>
/// Pure-function reader for Coach allowlist configuration entries. Extracted from the top-level
/// AppHost program so the exact same code path is testable without duplicating the scan logic.
/// </summary>
internal static class CoachConfigurationReader
{
    /// <summary>
    /// Maximum indexed entries scanned in <c>Coach:AllowedUserProfileIds:N</c>.
    /// </summary>
    internal const int MaxAllowedEntries = 16;

    /// <summary>
    /// Reads all nonblank <c>Coach:AllowedUserProfileIds:N</c> entries (N = 0..15) from
    /// configuration, preserving index order but compacting gaps. Returns an empty list when
    /// no entries are set, keeping the cohort fail-closed.
    /// </summary>
    internal static IReadOnlyList<string> ReadAllowedUserProfileIds(IConfiguration configuration)
    {
        return ReadAllowedUserProfileIdsWithDiagnostics(configuration).Ids;
    }

    /// <summary>
    /// Extended version that also reports which source indices were dropped as duplicates.
    /// The AppHost uses this to log a warning without exposing profile ID values.
    /// Comparison semantics: ordinal (case-sensitive), matching the API's
    /// <c>CoachOptionsValidator</c> which uses <c>HashSet&lt;string&gt;(StringComparer.Ordinal)</c>.
    /// </summary>
    internal static CoachAllowlistResult ReadAllowedUserProfileIdsWithDiagnostics(IConfiguration configuration)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateIndices = new List<int>();

        for (var i = 0; i < MaxAllowedEntries; i++)
        {
            var raw = configuration[$"Coach:AllowedUserProfileIds:{i}"]?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!seen.Add(raw))
            {
                duplicateIndices.Add(i);
                continue;
            }

            result.Add(raw);
        }

        return new CoachAllowlistResult(result, duplicateIndices);
    }
}
