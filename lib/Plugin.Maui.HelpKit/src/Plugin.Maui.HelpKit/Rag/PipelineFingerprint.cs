using System.Security.Cryptography;
using System.Text;

namespace Plugin.Maui.HelpKit.Rag;

/// <summary>
/// Computes a stable fingerprint identifying the full retrieval pipeline configuration.
/// Any change to the fingerprint requires a full content re-ingestion because it indicates
/// that previously-stored embeddings are no longer directly comparable to newly-generated
/// query embeddings (model change, dimension change, chunker change, etc.).
/// </summary>
public static class PipelineFingerprint
{
    /// <summary>
    /// Hash <c>{embeddingModelId}|{chunkerVersion}|{chunkSize}|{overlap}|{headingFormat}</c>
    /// as SHA-256 hex. Used as the cache key / invalidation signal for stored vectors.
    /// </summary>
    public static string Compute(
        string embeddingModelId,
        int chunkSize,
        int overlap,
        string chunkerVersion = MarkdownChunker.ChunkerVersion,
        string headingFormat = "breadcrumb")
    {
        if (string.IsNullOrWhiteSpace(embeddingModelId))
            throw new ArgumentException("Embedding model id is required.", nameof(embeddingModelId));

        var payload = string.Join('|', new[]
        {
            embeddingModelId.Trim().ToLowerInvariant(),
            (chunkerVersion ?? string.Empty).Trim().ToLowerInvariant(),
            chunkSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            overlap.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (headingFormat ?? string.Empty).Trim().ToLowerInvariant()
        });

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
