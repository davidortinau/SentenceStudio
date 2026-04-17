using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plugin.Maui.HelpKit.Storage;

/// <summary>
/// Per-question answer cache persisted in the <c>answer_cache</c> table.
/// Keys are SHA-256(normalized_question + "|" + pipeline_fingerprint) so any
/// fingerprint change invalidates all entries transparently.
/// </summary>
internal sealed class AnswerCache
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HelpKitDatabase _db;
    private readonly HelpKitOptions _options;
    private readonly ILogger<AnswerCache> _logger;

    public AnswerCache(HelpKitDatabase db, IOptions<HelpKitOptions> options, ILogger<AnswerCache> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds the deterministic cache key for a question under a given
    /// pipeline fingerprint.
    /// </summary>
    public static string ComputeKey(string question, string pipelineFingerprint)
    {
        var normalized = (question ?? string.Empty).Trim().ToLowerInvariant();
        var payload = normalized + "|" + (pipelineFingerprint ?? string.Empty);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task<(string Answer, IReadOnlyList<HelpKitCitation> Citations)?> TryGetAsync(
        string question, string pipelineFingerprint, CancellationToken ct = default)
    {
        if (!_options.EnableAnswerCache)
            return Task.FromResult<(string, IReadOnlyList<HelpKitCitation>)?>(null);

        var key = ComputeKey(question, pipelineFingerprint);
        lock (_db.SyncRoot)
        {
            var row = _db.Connection.Find<AnswerCacheRow>(key);
            if (row is null)
                return Task.FromResult<(string, IReadOnlyList<HelpKitCitation>)?>(null);

            if (row.ExpiresAt <= DateTime.UtcNow)
            {
                _db.Connection.Delete<AnswerCacheRow>(key);
                return Task.FromResult<(string, IReadOnlyList<HelpKitCitation>)?>(null);
            }

            var citations = MessageRepository.DeserializeCitations(row.CitationsJson);
            return Task.FromResult<(string, IReadOnlyList<HelpKitCitation>)?>((row.AnswerContent, citations));
        }
    }

    public Task PutAsync(
        string question,
        string pipelineFingerprint,
        string answer,
        IReadOnlyList<HelpKitCitation> citations,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        if (!_options.EnableAnswerCache) return Task.CompletedTask;

        var effectiveTtl = ttl ?? _options.AnswerCacheTtl;
        if (effectiveTtl <= TimeSpan.Zero) return Task.CompletedTask;

        var key = ComputeKey(question, pipelineFingerprint);
        var now = DateTime.UtcNow;
        var row = new AnswerCacheRow
        {
            QuestionHash = key,
            AnswerContent = answer ?? string.Empty,
            CitationsJson = citations is null || citations.Count == 0
                ? "[]"
                : JsonSerializer.Serialize(citations, s_json),
            CreatedAt = now,
            ExpiresAt = now + effectiveTtl,
        };

        lock (_db.SyncRoot)
        {
            _db.Connection.InsertOrReplace(row);
        }
        return Task.CompletedTask;
    }

    /// <summary>Nukes every cached answer. Called on pipeline fingerprint change.</summary>
    public Task InvalidateAllAsync(CancellationToken ct = default)
    {
        lock (_db.SyncRoot)
        {
            _db.Connection.DeleteAll<AnswerCacheRow>();
            _logger.LogInformation("Answer cache cleared.");
        }
        return Task.CompletedTask;
    }
}
