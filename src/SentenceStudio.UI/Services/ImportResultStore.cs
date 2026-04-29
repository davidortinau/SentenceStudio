using System.Collections.Concurrent;
using SentenceStudio.Services;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Stores committed import results in memory so the Import Complete view
/// survives browser back-navigation. Results expire after 30 minutes.
/// Singleton lifetime: single-user app, one Blazor circuit at a time.
/// </summary>
public interface IImportResultStore
{
    /// <summary>Save a result and return a cache key.</summary>
    Guid Save(ContentImportResult result);

    /// <summary>Retrieve a result by key, or null if expired/missing.</summary>
    ContentImportResult? TryGet(Guid key);
}

internal sealed class ImportResultStore : IImportResultStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, (ContentImportResult Result, DateTime ExpiresAt)> _cache = new();

    public Guid Save(ContentImportResult result)
    {
        Evict();
        var key = Guid.NewGuid();
        _cache[key] = (result, DateTime.UtcNow + Ttl);
        return key;
    }

    public ContentImportResult? TryGet(Guid key)
    {
        Evict();
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry.Result;
        return null;
    }

    private void Evict()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (kvp.Value.ExpiresAt <= now)
                _cache.TryRemove(kvp.Key, out _);
        }
    }
}
