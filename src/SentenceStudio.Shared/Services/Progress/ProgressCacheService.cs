using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services.Progress;

/// <summary>
/// PHASE 2 OPTIMIZATION: Simple in-memory cache for progress data.
/// All entries are keyed by userId to prevent cross-profile data bleed.
/// Backed by <see cref="ConcurrentDictionary{TKey, TValue}"/> because this service is
/// registered as a Singleton and read/written from many concurrent Blazor circuits + sync
/// callbacks. Plain Dictionary was a latent race (see Brot incident, 2026-06-12).
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
    private readonly ConcurrentDictionary<string, CacheEntry<VocabProgressSummary>> _vocabSummaryCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<PracticeHeatPoint>>> _practiceHeatCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<List<ResourceProgress>>> _resourceProgressCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<SkillProgress>> _skillProgressCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<TodaysPlan>> _todaysPlanCache = new();

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
        _vocabSummaryCache.TryRemove(UserId, out _);
    }

    public void InvalidatePracticeHeat()
    {
        _practiceHeatCache.TryRemove(UserId, out _);
    }

    public void InvalidateResourceProgress()
    {
        _resourceProgressCache.TryRemove(UserId, out _);
    }

    public void InvalidateSkillProgress(string skillId)
    {
        _skillProgressCache.TryRemove($"{UserId}:{skillId}", out _);
    }

    // Plan cache methods take an explicit date parameter to avoid timezone drift.
    // Callers MUST pass DateTime.UtcNow.Date (or whichever date semantics they need) — the cache
    // never invents its own "today". This eliminates the bug where the cache keyed on
    // DateTime.Today (LOCAL) while the rest of the plan pipeline keys on DateTime.UtcNow.Date,
    // causing cache misses and incorrect deletes near midnight UTC.
    // Regression-guarded by: ProgressCacheServiceTimezoneTests.cs

    public TodaysPlan? GetTodaysPlan(DateTime date)
    {
        var key = BuildPlanKey(date);
        if (!_todaysPlanCache.TryGetValue(key, out var entry) || entry.IsExpired())
            return null;

        _logger.LogDebug("Cache HIT: TodaysPlan for user {UserId}, date {Date:yyyy-MM-dd}", UserId, date.Date);
        return entry.Data;
    }

    public void SetTodaysPlan(DateTime date, TodaysPlan data)
    {
        var key = BuildPlanKey(date);
        var ttl = ComputePlanCacheTtl(date);
        _todaysPlanCache[key] = new CacheEntry<TodaysPlan>(data, ttl);
        _logger.LogDebug("Cache SET: TodaysPlan for user {UserId}, date {Date:yyyy-MM-dd}, expires in {Minutes}min", UserId, date.Date, (int)ttl.TotalMinutes);
    }

    public void InvalidateTodaysPlan(DateTime date)
    {
        var key = BuildPlanKey(date);
        _todaysPlanCache.TryRemove(key, out _);
    }

    public void UpdateTodaysPlan(DateTime date, TodaysPlan data)
    {
        var key = BuildPlanKey(date);
        var ttl = ComputePlanCacheTtl(date);
        _todaysPlanCache[key] = new CacheEntry<TodaysPlan>(data, ttl);
        _logger.LogDebug("Cache UPDATE: TodaysPlan for user {UserId}, date {Date:yyyy-MM-dd}", UserId, date.Date);
    }

    private string BuildPlanKey(DateTime date) => $"{UserId}:plan_{date.Date:yyyy-MM-dd}";

    private static TimeSpan ComputePlanCacheTtl(DateTime date)
    {
        // Cache entry expires at the next UTC midnight after the keyed date.
        // Floor at 1 minute to avoid negative or zero TTLs when the caller is past midnight
        // for the requested date (e.g., reconstructing yesterday's plan after midnight UTC).
        var expiresAt = DateTime.SpecifyKind(date.Date.AddDays(1), DateTimeKind.Utc);
        var ttl = expiresAt - DateTime.UtcNow;
        return ttl > TimeSpan.FromMinutes(1) ? ttl : TimeSpan.FromMinutes(1);
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
