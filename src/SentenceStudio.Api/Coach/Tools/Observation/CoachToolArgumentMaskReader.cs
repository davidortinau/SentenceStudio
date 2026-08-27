using Microsoft.Extensions.AI;

namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Reduces a call's arguments to which of them were present.
/// </summary>
/// <remarks>
/// <para>
/// The one place the seam looks at arguments at all, and it looks only at their names. No value is
/// read, copied, hashed, measured, or branched on — a length or a hash would still be a channel,
/// and a trace that recorded "the query was 14 characters" has recorded something about the
/// learner.
/// </para>
/// <para>
/// Names are matched case-insensitively because the serializer's naming policy is a detail this
/// reader should not depend on, and an argument whose name is unknown sets
/// <see cref="CoachToolArgumentMask.Unrecognized"/> rather than being ignored. Ignoring it would
/// let the mask fall behind the tool set silently, which is precisely what the enabled-registry
/// sweep exists to catch.
/// </para>
/// </remarks>
public static class CoachToolArgumentMaskReader
{
    /// <summary>
    /// The names the harness supplies that describe the call rather than the request, and which
    /// therefore contribute nothing to the mask.
    /// </summary>
    private static readonly string[] Ambient =
    [
        "ct", "cancellationtoken", "services", "context"
    ];

    /// <summary>The presence mask for <paramref name="arguments"/>.</summary>
    public static CoachToolArgumentMask Read(AIFunctionArguments? arguments)
    {
        if (arguments is null)
        {
            return CoachToolArgumentMask.None;
        }

        var mask = CoachToolArgumentMask.None;

        foreach (var (key, value) in arguments)
        {
            // A supplied-but-null optional argument is an argument the model chose not to use.
            // Recording it as present would report a default the tool applied as a decision the
            // model made.
            if (value is null)
            {
                continue;
            }

            mask |= Classify(key);
        }

        return mask;
    }

    private static CoachToolArgumentMask Classify(string key)
    {
        if (Array.Exists(Ambient, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase)))
        {
            return CoachToolArgumentMask.None;
        }

        if (Is(key, "window")) return CoachToolArgumentMask.Window;
        if (Is(key, "maxCategoryTags")) return CoachToolArgumentMask.MaxCategoryTags;
        if (Is(key, "maxResults")) return CoachToolArgumentMask.MaxResults;
        if (Is(key, "constraints")) return CoachToolArgumentMask.Constraints;
        if (Is(key, "query")) return CoachToolArgumentMask.Query;

        // The three id-taking reads collapse to one flag: the fact recorded is "the caller named a
        // single row", which is what CoachScopeFilters.SingleIdentifier already means.
        if (Is(key, "wordId") || Is(key, "skillId") || Is(key, "resourceId"))
        {
            return CoachToolArgumentMask.Identifier;
        }

        // Every write-intent tool takes one typed argument object under this name.
        if (Is(key, "arguments")) return CoachToolArgumentMask.WriteArguments;

        return CoachToolArgumentMask.Unrecognized;
    }

    private static bool Is(string key, string name) =>
        string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
}
