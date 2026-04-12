using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services.Progress;

/// <summary>
/// PHASE 2 OPTIMIZATION: Simple in-memory cache for progress data.
/// All entries are keyed by userId to prevent cross-profile data bleed.
/// </summary>
public class ProgressCacheService
{
    private readonly ILogger<ProgressCacheService> _logger;
    private readonly IPreferencesService _preferences;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

    public ProgressCacheService(ILogger<ProgressCacheService> logger, IPreferencesService preferences)
    {
        _logger = logger;
        _preferences = preferences;
    }

    // Cache entries keyed by userId
    private readonly Dictionary<string, CacheEntry<VocabProgressSummary>> _vocabSummaryCache = new();
    private readonly Dictionary<string, CacheEntry<IReadOnlyList<PracticeHeatPoint>>> _practiceHeatCache = new();
    private readonly Dictionary<string, CacheEntry<List<ResourceProgress>>> _resourceProgressCache = new();
    private readonly Dictionary<string, CacheEntry<SkillProgress>> _skillProgressCache = new();
    private readonly Dictionary<string, CacheEntry<TodaysPlan>> _todaysPlanCache = new();

    private string UserId => _preferences.Get("active_profile_id", string.Empty);

    public VocabProgressSummary? GetVocabSummary()
    {
        var key = UserId;
        if (!_vocabSummaryCache.TryGetValue(key, out var entry) || entry.IsExpired())
            return null;

        _logger.LogDebug("Cache HIT: VocabSummary for user {UserId}", key);
        return entry.Data;
    }

    public void SetVocabSummary(VocabProgressSummary data)
    {
        _vocabSummaryCache[UserId] = new CacheEntry<VocabProgressSummary>(data);
        _logger.LogDebug("Cache SET: VocabSummary for user {UserId}", UserId);
    }

    public IReadOnlyList<PracticeHeatPoint>? GetPracticeHeat()
    {
        var key = UserId;
        if (!_practiceHeatCache.TryGetValue(key, out var entry) || entry.IsExpired())
            return null;

        _logger.LogDebug("Cache HIT: PracticeHeat for user {UserId}", key);
        return entry.Data;
    }

    public void SetPracticeHeat(IReadOnlyList<PracticeHeatPoint> data)
    {
        _practiceHeatCache[UserId] = new CacheEntry<IReadOnlyList<PracticeHeatPoint>>(data);
        _logger.LogDebug("Cache SET: PracticeHeat for user {UserId}", UserId);
    }

    public List<ResourceProgress>? GetResourceProgress()
    {
        var key = UserId;
        if (!_resourceProgressCache.TryGetValue(key, out var entry) || entry.IsExpired())
            return null;

        _logger.LogDebug("Cache HIT: ResourceProgress for user {UserId}", key);
        return entry.Data;
    }

    public void SetResourceProgress(List<ResourceProgress> data)
    {
        _resourceProgressCache[UserId] = new CacheEntry<List<ResourceProgress>>(data);
        _logger.LogDebug("Cache SET: ResourceProgress for user {UserId}", UserId);
    }

    public SkillProgress? GetSkillProgress(string skillId)
    {
        var key = $"{UserId}:{skillId}";
        if (!_skillProgressCache.TryGetValue(key, out var entry) || entry.IsExpired())
            return null;

        _logger.LogDebug("Cache HIT: SkillProgress for skill {SkillId}, user {UserId}", skillId, UserId);
        return entry.Data;
    }

    public void SetSkillProgress(string skillId, SkillProgress data)
    {
        _skillProgressCache[$"{UserId}:{skillId}"] = new CacheEntry<SkillProgress>(data);
        _logger.LogDebug("Cache SET: SkillProgress for skill {SkillId}, user {UserId}", skillId, UserId);
    }

    /// <summary>
    /// Invalidate all caches for all users
    /// </summary>
    public void InvalidateAll()
    {
        _vocabSummaryCache.Clear();
        _practiceHeatCache.Clear();
        _resourceProgressCache.Clear();
        _skillProgressCache.Clear();
        _todaysPlanCache.Clear();
        _logger.LogDebug("Cache INVALIDATED: All");
    }

    public void InvalidateVocabSummary()
    {
        _vocabSummaryCache.Remove(UserId);
    }

    public void InvalidatePracticeHeat()
    {
        _practiceHeatCache.Remove(UserId);
    }

    public void InvalidateResourceProgress()
    {
        _resourceProgressCache.Remove(UserId);
    }

    public void InvalidateSkillProgress(string skillId)
    {
        _skillProgressCache.Remove($"{UserId}:{skillId}");
    }

    public TodaysPlan? GetTodaysPlan()
    {
        var key = UserId;
        if (!_todaysPlanCache.TryGetValue(key, out var entry) || entry.IsExpired())
            return null;

        _logger.LogDebug("Cache HIT: TodaysPlan for user {UserId}", key);
        return entry.Data;
    }

    public void SetTodaysPlan(TodaysPlan data)
    {
        _todaysPlanCache[UserId] = new CacheEntry<TodaysPlan>(data);
        _logger.LogDebug("Cache SET: TodaysPlan for user {UserId}", UserId);
    }

    public void InvalidateTodaysPlan()
    {
        _todaysPlanCache.Remove(UserId);
    }

    public void UpdateTodaysPlan(TodaysPlan data)
    {
        _todaysPlanCache[UserId] = new CacheEntry<TodaysPlan>(data);
        _logger.LogDebug("Cache UPDATE: TodaysPlan for user {UserId}", UserId);
    }

    private class CacheEntry<T>
    {
        public T Data { get; }
        public DateTime ExpiresAt { get; }

        public CacheEntry(T data, TimeSpan? expiration = null)
        {
            Data = data;
            ExpiresAt = DateTime.UtcNow.Add(expiration ?? TimeSpan.FromMinutes(5));
        }

        public bool IsExpired() => DateTime.UtcNow >= ExpiresAt;
    }
}
