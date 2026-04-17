using System.Text.RegularExpressions;

namespace Plugin.Maui.HelpKit.Rag;

/// <summary>
/// Verified citation attached to a chat answer. Rendered in the UI as a tappable source chip.
/// </summary>
public readonly record struct HelpKitCitation(string SourcePath, string SectionAnchor, string HeadingPath);

/// <summary>
/// Result of running the LLM output through the citation validator.
/// </summary>
/// <param name="CleanedContent">LLM content with invalid citations replaced by <c>[cite unverified]</c>.</param>
/// <param name="ValidCitations">De-duplicated list of citations that resolved to a retrieved chunk.</param>
/// <param name="InvalidCitations">Raw citation markers that did not match any retrieved chunk.</param>
public sealed record ValidatedAnswer(
    string CleanedContent,
    IReadOnlyList<HelpKitCitation> ValidCitations,
    IReadOnlyList<string> InvalidCitations);

/// <summary>
/// Parses LLM output for <c>[cite:path#anchor]</c> markers and validates each against the
/// set of retrieved chunks used to ground the answer. Invalid citations are stripped from
/// the content to prevent the UI from surfacing hallucinated sources.
/// </summary>
public static class CitationValidator
{
    /// <summary>Marker inserted in place of unverifiable citations.</summary>
    public const string UnverifiedMarker = "[cite unverified]";

    // [cite:relative/path.md#anchor-name]
    private static readonly Regex CitationRegex = new(
        @"\[cite:(?<path>[^\]#\s]+)(?:#(?<anchor>[^\]\s]+))?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Validate every citation in <paramref name="llmOutput"/> against <paramref name="retrievedChunks"/>.
    /// </summary>
    public static ValidatedAnswer Validate(string llmOutput, IReadOnlyList<HelpKitChunk> retrievedChunks)
    {
        if (string.IsNullOrEmpty(llmOutput))
            return new ValidatedAnswer(string.Empty, Array.Empty<HelpKitCitation>(), Array.Empty<string>());

        var index = BuildIndex(retrievedChunks ?? Array.Empty<HelpKitChunk>());
        var valid = new Dictionary<string, HelpKitCitation>(StringComparer.OrdinalIgnoreCase);
        var invalid = new List<string>();

        var cleaned = CitationRegex.Replace(llmOutput, match =>
        {
            var path = match.Groups["path"].Value;
            var anchor = match.Groups["anchor"].Success ? match.Groups["anchor"].Value : string.Empty;
            var key = $"{path}#{anchor}";

            if (index.TryGetValue(key, out var chunk))
            {
                valid[key] = new HelpKitCitation(chunk.SourcePath, chunk.SectionAnchor, chunk.HeadingPath);
                return match.Value; // keep the original marker in the content
            }

            // Fall back to path-only match (LLM may omit anchors occasionally).
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var kv in index)
                {
                    if (string.Equals(kv.Value.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        valid[key] = new HelpKitCitation(kv.Value.SourcePath, kv.Value.SectionAnchor, kv.Value.HeadingPath);
                        return match.Value;
                    }
                }
            }

            invalid.Add(match.Value);
            return UnverifiedMarker;
        });

        return new ValidatedAnswer(cleaned, valid.Values.ToList(), invalid);
    }

    /// <summary>
    /// Produces a UI-ready answer: the cleaned content with citation markers removed and
    /// citations surfaced separately so the view can render them as chips.
    /// </summary>
    public static string RenderForDisplay(ValidatedAnswer validated)
    {
        if (validated is null) throw new ArgumentNullException(nameof(validated));
        // Strip citation markers from the prose; the UI renders citations as separate chips below.
        var withoutMarkers = CitationRegex.Replace(validated.CleanedContent, string.Empty);
        // Collapse any double-spaces that result from stripping inline markers.
        withoutMarkers = Regex.Replace(withoutMarkers, @"[ \t]{2,}", " ");
        withoutMarkers = Regex.Replace(withoutMarkers, @" +([,.;:!?])", "$1");
        return withoutMarkers.Trim();
    }

    private static Dictionary<string, HelpKitChunk> BuildIndex(IReadOnlyList<HelpKitChunk> chunks)
    {
        var map = new Dictionary<string, HelpKitChunk>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            var key = $"{chunk.SourcePath}#{chunk.SectionAnchor}";
            map[key] = chunk;
        }
        return map;
    }
}
