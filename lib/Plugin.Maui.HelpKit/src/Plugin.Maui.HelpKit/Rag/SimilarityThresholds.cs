namespace Plugin.Maui.HelpKit.Rag;

/// <summary>
/// Per-embedding-model default cosine similarity thresholds for the "below-threshold → refuse"
/// retrieval gate. Values reflect empirical tuning on RAG workloads (not generic STS benchmarks)
/// and are conservative: below the threshold, HelpKit refuses rather than risking a grounded-looking
/// hallucination. Devs can override via <c>HelpKitOptions.SimilarityThresholdOverride</c>.
/// </summary>
public static class SimilarityThresholds
{
    /// <summary>
    /// Returns the recommended threshold for a known embedding-model identifier. Unknown models
    /// fall back to a conservative default (0.40) so new-model behaviour fails safe.
    /// </summary>
    public static double DefaultFor(string embeddingModelId) => (embeddingModelId ?? string.Empty).ToLowerInvariant() switch
    {
        var m when m.Contains("text-embedding-3-small") => 0.35,
        var m when m.Contains("text-embedding-3-large") => 0.40,
        var m when m.Contains("text-embedding-ada") => 0.75,
        var m when m.Contains("minilm") || m.Contains("all-minilm") => 0.55,
        var m when m.Contains("bge") => 0.60,
        _ => 0.40
    };
}
