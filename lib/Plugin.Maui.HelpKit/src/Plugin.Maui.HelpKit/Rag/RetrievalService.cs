using Microsoft.Extensions.AI;

namespace Plugin.Maui.HelpKit.Rag;

/// <summary>
/// Outcome of a retrieval request. If <see cref="ShouldRefuse"/> is <c>true</c>, the chat
/// session must skip the LLM call and return the configured refusal message — this is the
/// "below-threshold gate" that prevents hallucination on unsupported questions.
/// </summary>
/// <param name="Chunks">Retrieved chunks above the similarity threshold (empty when refusing).</param>
/// <param name="TopScore">Best similarity score observed for this query (for telemetry).</param>
/// <param name="ShouldRefuse">True when no chunk cleared the threshold.</param>
public sealed record RetrievalResult(
    IReadOnlyList<HelpKitChunk> Chunks,
    double TopScore,
    bool ShouldRefuse);

/// <summary>Retrieves the most relevant documentation chunks for a user query.</summary>
public interface IRetrievalService
{
    /// <summary>
    /// Embed the query, cosine-rank against the vector store, apply threshold, return chunks.
    /// </summary>
    Task<RetrievalResult> RetrieveAsync(string query, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RetrievalService : IRetrievalService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly string _embeddingModelId;
    private readonly double _threshold;
    private readonly int _topK;

    public RetrievalService(
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        string embeddingModelId,
        int topK = 5,
        double? similarityThresholdOverride = null)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _embeddingModelId = embeddingModelId ?? throw new ArgumentNullException(nameof(embeddingModelId));
        _topK = topK > 0 ? topK : 5;
        _threshold = similarityThresholdOverride ?? SimilarityThresholds.DefaultFor(embeddingModelId);
    }

    /// <remarks>
    /// Vector retrieval deferred — VectorData is [Experimental] and no consuming feature
    /// is on the roadmap yet. See .squad/decisions.md M.E.AI 10.5 cycle.
    /// </remarks>
    [Obsolete("Vector retrieval deferred — see .squad/decisions.md M.E.AI 10.5 cycle")]
    public Task<RetrievalResult> RetrieveAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(new RetrievalResult(Array.Empty<HelpKitChunk>(), 0.0, true));

        // TODO: vector retrieval pending — see .squad/decisions.md M.E.AI 10.5 cycle.
        // When a "practice more like this" or transcript-search feature lands, wire this to
        // the VectorStore.SearchAsync path (reference flow preserved in git history).
        return Task.FromResult(new RetrievalResult(Array.Empty<HelpKitChunk>(), 0.0, ShouldRefuse: true));
    }

    /// <summary>Cosine similarity between two equal-length vectors. Public for unit testing.</summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vector length mismatch.");
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA <= 0 || magB <= 0) return 0.0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
