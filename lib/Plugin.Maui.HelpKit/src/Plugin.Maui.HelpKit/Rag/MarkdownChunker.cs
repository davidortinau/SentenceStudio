using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Plugin.Maui.HelpKit.Rag;

/// <summary>
/// A single retrievable fragment of source documentation.
/// </summary>
/// <param name="Id">Stable content-addressable identifier (SHA-256 of SourcePath + anchor + content).</param>
/// <param name="SourcePath">Relative path of the source file (for citations).</param>
/// <param name="HeadingPath">Breadcrumb of headings leading to this chunk (e.g. "Vocabulary &gt; Adding a word").</param>
/// <param name="SectionAnchor">GitHub-style kebab-case anchor of the nearest heading.</param>
/// <param name="Content">Chunk text content (headings prepended for retrieval context).</param>
public readonly record struct HelpKitChunk(
    string Id,
    string SourcePath,
    string HeadingPath,
    string SectionAnchor,
    string Content);

/// <summary>
/// Paragraph-aware markdown chunker. Produces ~512-token chunks with 128-token overlap,
/// maintains a heading breadcrumb per chunk, and records a GitHub-style slug as the anchor.
/// </summary>
/// <remarks>
/// Token counts are approximated using the 4-chars-per-token heuristic so that the library
/// has no runtime dependency on a tokenizer. This is acceptable because the vector retrieval
/// quality is tolerant of modest chunk-size variance; the fingerprint will invalidate the
/// ingest if <see cref="ChunkerVersion"/> or the configured chunk size changes.
/// </remarks>
public static class MarkdownChunker
{
    /// <summary>
    /// Version stamp embedded in the pipeline fingerprint. Bump whenever chunker behavior
    /// changes in a way that should force re-ingestion of all content.
    /// </summary>
    public const string ChunkerVersion = "v1";

    /// <summary>Default target token count per chunk.</summary>
    public const int DefaultChunkSizeTokens = 512;

    /// <summary>Default overlap in tokens between adjacent chunks.</summary>
    public const int DefaultOverlapTokens = 128;

    private const int CharsPerToken = 4;

    private static readonly Regex HeadingRegex = new(
        @"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SlugStripRegex = new(@"[^a-z0-9\s-]", RegexOptions.Compiled);
    private static readonly Regex SlugCollapseRegex = new(@"[\s-]+", RegexOptions.Compiled);

    /// <summary>
    /// Chunks a markdown document using default size and overlap.
    /// </summary>
    public static IReadOnlyList<HelpKitChunk> Chunk(string markdown, string sourcePath)
        => Chunk(markdown, sourcePath, DefaultChunkSizeTokens, DefaultOverlapTokens);

    /// <summary>
    /// Chunks a markdown document at paragraph boundaries, targeting <paramref name="chunkSizeTokens"/>
    /// with <paramref name="overlapTokens"/> of context bridging across chunks.
    /// </summary>
    public static IReadOnlyList<HelpKitChunk> Chunk(
        string markdown,
        string sourcePath,
        int chunkSizeTokens,
        int overlapTokens)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Array.Empty<HelpKitChunk>();
        if (chunkSizeTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeTokens));
        if (overlapTokens < 0 || overlapTokens >= chunkSizeTokens)
            throw new ArgumentOutOfRangeException(nameof(overlapTokens));

        var targetChars = chunkSizeTokens * CharsPerToken;
        var overlapChars = overlapTokens * CharsPerToken;

        var blocks = SplitBlocks(markdown);
        var breadcrumb = new string[6]; // H1..H6
        var nearestAnchor = string.Empty;

        var chunks = new List<HelpKitChunk>();
        var buffer = new StringBuilder();
        string bufferHeadingPath = string.Empty;
        string bufferAnchor = string.Empty;

        void Flush()
        {
            if (buffer.Length == 0) return;
            var content = buffer.ToString().Trim();
            if (content.Length == 0) { buffer.Clear(); return; }
            var id = ComputeChunkId(sourcePath, bufferAnchor, content);
            chunks.Add(new HelpKitChunk(id, sourcePath, bufferHeadingPath, bufferAnchor, content));
            buffer.Clear();
        }

        foreach (var block in blocks)
        {
            var headingMatch = HeadingRegex.Match(block);
            if (headingMatch.Success)
            {
                // Heading boundaries are always chunk boundaries — keeps breadcrumbs honest.
                Flush();
                var level = headingMatch.Groups["hashes"].Value.Length;
                var title = headingMatch.Groups["title"].Value.Trim();
                breadcrumb[level - 1] = title;
                for (int i = level; i < breadcrumb.Length; i++) breadcrumb[i] = null!;
                nearestAnchor = Slugify(title);
                // Prepend heading as the first line of the next chunk for retrieval context.
                bufferHeadingPath = BuildBreadcrumb(breadcrumb);
                bufferAnchor = nearestAnchor;
                buffer.AppendLine(new string('#', level) + " " + title);
                continue;
            }

            if (buffer.Length == 0)
            {
                bufferHeadingPath = BuildBreadcrumb(breadcrumb);
                bufferAnchor = nearestAnchor;
            }

            if (buffer.Length > 0 && buffer.Length + block.Length + 2 > targetChars)
            {
                // Carry the tail of the previous chunk forward as overlap context.
                var previousTail = overlapChars > 0 && buffer.Length > overlapChars
                    ? buffer.ToString(buffer.Length - overlapChars, overlapChars)
                    : string.Empty;
                Flush();
                if (!string.IsNullOrEmpty(previousTail))
                {
                    buffer.AppendLine(previousTail.Trim());
                    bufferHeadingPath = BuildBreadcrumb(breadcrumb);
                    bufferAnchor = nearestAnchor;
                }
            }

            if (buffer.Length > 0) buffer.AppendLine();
            buffer.AppendLine(block);
        }

        Flush();
        return chunks;
    }

    /// <summary>
    /// Produces a GitHub-style slug from heading text (lowercase, non-alphanumeric stripped, spaces to dashes).
    /// </summary>
    public static string Slugify(string headingText)
    {
        if (string.IsNullOrWhiteSpace(headingText)) return string.Empty;
        var lower = headingText.Trim().ToLower(CultureInfo.InvariantCulture);
        lower = SlugStripRegex.Replace(lower, string.Empty);
        lower = SlugCollapseRegex.Replace(lower, "-");
        return lower.Trim('-');
    }

    private static string BuildBreadcrumb(IReadOnlyList<string> levels)
    {
        var parts = new List<string>();
        for (int i = 0; i < levels.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(levels[i])) parts.Add(levels[i]);
        }
        return string.Join(" > ", parts);
    }

    private static IEnumerable<string> SplitBlocks(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = Regex.Split(normalized, "\n\n+");
        foreach (var part in parts)
        {
            var trimmed = part.Trim('\n');
            if (!string.IsNullOrWhiteSpace(trimmed))
                yield return trimmed;
        }
    }

    private static string ComputeChunkId(string sourcePath, string anchor, string content)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes($"{sourcePath}|{anchor}|{content}");
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
