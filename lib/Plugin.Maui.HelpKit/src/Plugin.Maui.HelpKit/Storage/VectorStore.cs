using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Plugin.Maui.HelpKit.Rag;

namespace Plugin.Maui.HelpKit.Storage;

/// <summary>
/// A chunk record persisted in the in-memory vector store. Mirrors
/// <see cref="HelpKitChunk"/> and adds the embedding vector.
/// </summary>
public sealed record HelpKitChunkRecord(
    string Id,
    string SourcePath,
    string HeadingPath,
    string SectionAnchor,
    string Content,
    float[] Embedding);

/// <summary>
/// In-memory vector store with gzip-compressed JSON persistence.
/// </summary>
/// <remarks>
/// <para>
/// Design notes: the Alpha contract calls for
/// <c>Microsoft.Extensions.VectorData</c>'s in-memory adapter. We keep the
/// dependency footprint at just the <em>Abstractions</em> package and
/// implement search directly against a <see cref="List{T}"/> using the
/// cosine similarity helper already shipped in
/// <see cref="RetrievalService.CosineSimilarity(ReadOnlySpan{float},ReadOnlySpan{float})"/>.
/// This keeps the preview-package surface narrow and the behaviour easy to
/// reason about; we can swap to the first-party in-memory store in Beta if
/// it graduates to stable without changing callers.
/// </para>
/// <para>
/// Thread-safety: writes are serialized on an internal lock. Reads take a
/// snapshot of the list so concurrent searches do not block on each other.
/// </para>
/// </remarks>
internal sealed class VectorStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly ILogger<VectorStore> _logger;
    private readonly HelpKitDatabase _db;
    private readonly object _lock = new();
    private List<HelpKitChunkRecord> _records = new();
    private bool _hydrated;

    public VectorStore(HelpKitDatabase db, ILogger<VectorStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Number of records currently in the store.</summary>
    public int Count
    {
        get
        {
            EnsureHydrated();
            lock (_lock) return _records.Count;
        }
    }

    /// <summary>
    /// Upserts a batch of records (identity = <see cref="HelpKitChunkRecord.Id"/>)
    /// and persists the full collection to disk.
    /// </summary>
    public async Task UpsertAsync(IEnumerable<HelpKitChunkRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        EnsureHydrated();

        lock (_lock)
        {
            var index = _records.ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (var rec in records)
            {
                if (rec is null) continue;
                index[rec.Id] = rec;
            }
            _records = index.Values.ToList();
        }

        await PersistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the top-<paramref name="topK"/> records by cosine similarity
    /// to <paramref name="queryVector"/>, descending.
    /// </summary>
    public Task<IReadOnlyList<(HelpKitChunkRecord Chunk, double Score)>> SearchAsync(
        float[] queryVector, int topK, CancellationToken ct = default)
    {
        if (queryVector is null || queryVector.Length == 0)
            return Task.FromResult<IReadOnlyList<(HelpKitChunkRecord, double)>>(Array.Empty<(HelpKitChunkRecord, double)>());
        if (topK <= 0) topK = 5;

        EnsureHydrated();

        List<HelpKitChunkRecord> snapshot;
        lock (_lock) snapshot = _records.ToList();

        var scored = new List<(HelpKitChunkRecord Chunk, double Score)>(snapshot.Count);
        foreach (var rec in snapshot)
        {
            if (rec.Embedding is null || rec.Embedding.Length != queryVector.Length) continue;
            var score = RetrievalService.CosineSimilarity(queryVector, rec.Embedding);
            scored.Add((rec, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (scored.Count > topK) scored.RemoveRange(topK, scored.Count - topK);
        return Task.FromResult<IReadOnlyList<(HelpKitChunkRecord, double)>>(scored);
    }

    /// <summary>Drops all records and deletes the on-disk file.</summary>
    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        lock (_lock) _records = new List<HelpKitChunkRecord>();

        try
        {
            if (File.Exists(_db.VectorsJsonPath))
                File.Delete(_db.VectorsJsonPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete vectors file at {Path}", _db.VectorsJsonPath);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void EnsureHydrated()
    {
        if (_hydrated) return;
        lock (_lock)
        {
            if (_hydrated) return;
            try
            {
                if (File.Exists(_db.VectorsJsonPath))
                {
                    using var fs = File.OpenRead(_db.VectorsJsonPath);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    var loaded = JsonSerializer.Deserialize<List<HelpKitChunkRecord>>(gz, s_json);
                    if (loaded is not null)
                    {
                        _records = loaded;
                        _logger.LogInformation(
                            "Hydrated {Count} vector records from {Path}",
                            loaded.Count, _db.VectorsJsonPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to hydrate vector store from {Path}; starting empty.",
                    _db.VectorsJsonPath);
                _records = new List<HelpKitChunkRecord>();
            }
            _hydrated = true;
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        List<HelpKitChunkRecord> snapshot;
        lock (_lock) snapshot = _records.ToList();

        Directory.CreateDirectory(_db.StorageDirectory);
        var tmpPath = _db.VectorsJsonPath + ".tmp";
        try
        {
            await using (var fs = File.Create(tmpPath))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            {
                await JsonSerializer.SerializeAsync(gz, snapshot, s_json, ct).ConfigureAwait(false);
            }

            // Atomic replace so a crash mid-write never leaves a truncated file.
            if (File.Exists(_db.VectorsJsonPath))
                File.Replace(tmpPath, _db.VectorsJsonPath, destinationBackupFileName: null);
            else
                File.Move(tmpPath, _db.VectorsJsonPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist vector store to {Path}", _db.VectorsJsonPath);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort */ }
            throw;
        }
    }
}
