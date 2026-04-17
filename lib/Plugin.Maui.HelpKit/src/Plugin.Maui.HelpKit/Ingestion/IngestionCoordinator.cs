using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plugin.Maui.HelpKit.Diagnostics;
using Plugin.Maui.HelpKit.Rag;
using Plugin.Maui.HelpKit.Storage;

namespace Plugin.Maui.HelpKit.Ingestion;

/// <summary>
/// Orchestrates a full ingestion pass: fingerprint → discover → chunk →
/// redact → embed → upsert → persist. Completes River's
/// <see cref="Rag.IngestionOrchestrator"/> stub with concrete storage wiring.
/// </summary>
/// <remarks>
/// Flow:
/// <list type="number">
/// <item>Compute the current pipeline fingerprint from the embedding model id.</item>
/// <item>Compare against the stored fingerprint; no-op if unchanged and the
/// vector store already has chunks.</item>
/// <item>Otherwise: clear vectors, walk the markdown source, chunk each
/// file, redact every chunk, embed in batches of <see cref="BatchSize"/>,
/// upsert to the vector store.</item>
/// <item>Record the new fingerprint row; invalidate the answer cache.</item>
/// </list>
/// </remarks>
internal sealed class IngestionCoordinator
{
    private const int BatchSize = 16;
    private const string FallbackModelId = "helpkit-embedding-unknown";

    private readonly HelpKitDatabase _db;
    private readonly HelpKitOptions _options;
    private readonly VectorStore _vectorStore;
    private readonly FileIngestionSource _source;
    private readonly AnswerCache _answerCache;
    private readonly IHelpKitContentFilter _contentFilter;
    private readonly HelpKitAiResolver _ai;
    private readonly ILogger<IngestionCoordinator> _logger;

    public IngestionCoordinator(
        HelpKitDatabase db,
        IOptions<HelpKitOptions> options,
        VectorStore vectorStore,
        FileIngestionSource source,
        AnswerCache answerCache,
        IHelpKitContentFilter contentFilter,
        HelpKitAiResolver ai,
        ILogger<IngestionCoordinator> logger)
    {
        _db = db;
        _options = options.Value;
        _vectorStore = vectorStore;
        _source = source;
        _answerCache = answerCache;
        _contentFilter = contentFilter;
        _ai = ai;
        _logger = logger;
    }

    /// <summary>
    /// Runs an ingestion pass. Safe to call at every startup; fingerprint
    /// gating makes re-runs cheap when nothing has changed.
    /// </summary>
    public async Task<IngestionResult> IngestAsync(CancellationToken ct = default)
    {
        // Resolve the embedding generator up front — surfaces missing-DI
        // errors with a clear remediation message before we do any work.
        var embeddings = _ai.ResolveEmbeddingGenerator();
        var modelId = ResolveModelId(embeddings);
        var fingerprint = PipelineFingerprint.Compute(
            modelId,
            MarkdownChunker.DefaultChunkSizeTokens,
            MarkdownChunker.DefaultOverlapTokens);

        var stored = ReadStoredFingerprint();
        if (stored is not null
            && string.Equals(stored.Fingerprint, fingerprint, StringComparison.Ordinal)
            && stored.ChunkCount > 0
            && _vectorStore.Count > 0)
        {
            _logger.LogInformation(
                "HelpKit ingestion skipped; fingerprint unchanged ({Fingerprint}) and {Count} chunks already indexed.",
                fingerprint, stored.ChunkCount);
            return new IngestionResult(Skipped: true, ChunkCount: stored.ChunkCount, Fingerprint: fingerprint);
        }

        _logger.LogInformation(
            "HelpKit ingestion starting. Model={Model} Fingerprint={Fingerprint}", modelId, fingerprint);

        await _vectorStore.DeleteAllAsync(ct).ConfigureAwait(false);
        await _answerCache.InvalidateAllAsync(ct).ConfigureAwait(false);

        int totalChunks = 0;
        int totalFiles = 0;
        int totalErrors = 0;
        var pending = new List<HelpKitChunk>();

        foreach (var (path, content) in _source.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            totalFiles++;

            IReadOnlyList<HelpKitChunk> chunks;
            try
            {
                var redacted = _contentFilter.Redact(content);
                chunks = MarkdownChunker.Chunk(redacted, path);
            }
            catch (Exception ex)
            {
                totalErrors++;
                HelpKitMetrics.Increment(HelpKitMetrics.IngestErrors);
                _logger.LogWarning(ex, "Chunking failed for {Path}; skipping.", path);
                continue;
            }

            foreach (var chunk in chunks)
            {
                pending.Add(chunk);
                if (pending.Count >= BatchSize)
                {
                    totalChunks += await FlushBatchAsync(embeddings, pending, ct).ConfigureAwait(false);
                    pending.Clear();
                }
            }
        }

        if (pending.Count > 0)
        {
            totalChunks += await FlushBatchAsync(embeddings, pending, ct).ConfigureAwait(false);
            pending.Clear();
        }

        WriteStoredFingerprint(fingerprint, totalChunks);

        HelpKitMetrics.Increment(HelpKitMetrics.IngestChunks, totalChunks);
        _logger.LogInformation(
            "HelpKit ingestion complete. Files={Files} Chunks={Chunks} Errors={Errors} Fingerprint={Fingerprint}",
            totalFiles, totalChunks, totalErrors, fingerprint);

        return new IngestionResult(Skipped: false, ChunkCount: totalChunks, Fingerprint: fingerprint);
    }

    /// <summary>Current pipeline fingerprint — exposed for retrieval / cache key building.</summary>
    public string GetCurrentFingerprint()
    {
        var embeddings = _ai.ResolveEmbeddingGenerator();
        var modelId = ResolveModelId(embeddings);
        return PipelineFingerprint.Compute(
            modelId,
            MarkdownChunker.DefaultChunkSizeTokens,
            MarkdownChunker.DefaultOverlapTokens);
    }

    /// <summary>Exposed for retrieval so it shares the same model-id-resolution policy.</summary>
    public string GetEmbeddingModelId() => ResolveModelId(_ai.ResolveEmbeddingGenerator());

    private async Task<int> FlushBatchAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IReadOnlyList<HelpKitChunk> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0) return 0;

        GeneratedEmbeddings<Embedding<float>> result;
        try
        {
            result = await embeddings.GenerateAsync(
                batch.Select(b => b.Content).ToArray(), options: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HelpKitMetrics.Increment(HelpKitMetrics.IngestErrors);
            _logger.LogError(ex, "Embedding batch failed; {Count} chunks dropped.", batch.Count);
            return 0;
        }

        var records = new List<HelpKitChunkRecord>(batch.Count);
        for (int i = 0; i < batch.Count && i < result.Count; i++)
        {
            var embedding = result[i];
            var vector = embedding.Vector.ToArray();
            records.Add(new HelpKitChunkRecord(
                Id: batch[i].Id,
                SourcePath: batch[i].SourcePath,
                HeadingPath: batch[i].HeadingPath,
                SectionAnchor: batch[i].SectionAnchor,
                Content: batch[i].Content,
                Embedding: vector));
        }

        await _vectorStore.UpsertAsync(records, ct).ConfigureAwait(false);
        return records.Count;
    }

    private IngestionFingerprintRow? ReadStoredFingerprint()
    {
        lock (_db.SyncRoot)
        {
            return _db.Connection.Find<IngestionFingerprintRow>(1);
        }
    }

    private void WriteStoredFingerprint(string fingerprint, int chunkCount)
    {
        var row = new IngestionFingerprintRow
        {
            Id = 1,
            Fingerprint = fingerprint,
            IngestedAt = DateTime.UtcNow,
            ChunkCount = chunkCount,
        };
        lock (_db.SyncRoot)
        {
            _db.Connection.InsertOrReplace(row);
        }
    }

    private static string ResolveModelId(IEmbeddingGenerator<string, Embedding<float>> embeddings)
    {
        // M.E.AI exposes EmbeddingGeneratorMetadata via GetService.
        try
        {
            var metadata = embeddings.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;
            if (metadata is not null)
            {
                if (!string.IsNullOrWhiteSpace(metadata.DefaultModelId))
                    return metadata.DefaultModelId!;
                if (!string.IsNullOrWhiteSpace(metadata.ProviderName))
                    return metadata.ProviderName!;
            }
        }
        catch
        {
            // Metadata access is optional — fall through to the fallback id.
        }
        return FallbackModelId;
    }
}

/// <summary>Outcome of an <see cref="IngestionCoordinator.IngestAsync"/> run.</summary>
internal sealed record IngestionResult(bool Skipped, int ChunkCount, string Fingerprint);
