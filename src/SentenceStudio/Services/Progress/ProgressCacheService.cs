namespace SentenceStudio.Services.Progress;

/// <summary>
/// PHASE 2 OPTIMIZATION: Simple in-memory cache for progress data
/// Reduces database queries on return visits to the dashboard
/// </summary>
public class ProgressCacheService
{
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

    // Cache entries
    private CacheEntry<VocabProgressSummary>? _vocabSummaryCache;
    private CacheEntry<IReadOnlyList<PracticeHeatPoint>>? _practiceHeatCache;
    private CacheEntry<List<ResourceProgress>>? _resourceProgressCache;
    private readonly Dictionary<int, CacheEntry<SkillProgress>> _skillProgressCache = new();

    /// <summary>
    /// Get cached vocab summary or null if expired/not cached
    /// </summary>
    public VocabProgressSummary? GetVocabSummary()
    {
        if (_vocabSummaryCache?.IsExpired() != false)
            return null;

        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Cache HIT: VocabSummary");
        return _vocabSummaryCache.Data;
    }

    /// <summary>
    /// Cache vocab summary data
    /// </summary>
    public void SetVocabSummary(VocabProgressSummary data)
    {
        _vocabSummaryCache = new CacheEntry<VocabProgressSummary>(data);
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Cache SET: VocabSummary");
    }

    /// <summary>
    /// Get cached practice heat data or null if expired/not cached
    /// </summary>
    public IReadOnlyList<PracticeHeatPoint>? GetPracticeHeat()
    {
        if (_practiceHeatCache?.IsExpired() != false)
            return null;

        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Cache HIT: PracticeHeat");
        return _practiceHeatCache.Data;
    }

    /// <summary>
    /// Cache practice heat data
    /// </summary>
    public void SetPracticeHeat(IReadOnlyList<PracticeHeatPoint> data)
    {
        _practiceHeatCache = new CacheEntry<IReadOnlyList<PracticeHeatPoint>>(data);
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Cache SET: PracticeHeat");
    }

    /// <summary>
    /// Get cached resource progress or null if expired/not cached
    /// </summary>
    public List<ResourceProgress>? GetResourceProgress()
    {
        if (_resourceProgressCache?.IsExpired() != false)
            return null;

        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Cache HIT: ResourceProgress");
        return _resourceProgressCache.Data;
    }

    /// <summary>
    /// Cache resource progress data
    /// </summary>
    public void SetResourceProgress(List<ResourceProgress> data)
    {
        _resourceProgressCache = new CacheEntry<List<ResourceProgress>>(data);
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Cache SET: ResourceProgress");
    }

    /// <summary>
    /// Get cached skill progress for a specific skill or null if expired/not cached
    /// </summary>
    public SkillProgress? GetSkillProgress(int skillId)
    {
        if (!_skillProgressCache.TryGetValue(skillId, out var entry) || entry.IsExpired())
            return null;

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Cache HIT: SkillProgress for skill {skillId}");
        return entry.Data;
    }

    /// <summary>
    /// Cache skill progress data for a specific skill
    /// </summary>
    public void SetSkillProgress(int skillId, SkillProgress data)
    {
        _skillProgressCache[skillId] = new CacheEntry<SkillProgress>(data);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Cache SET: SkillProgress for skill {skillId}");
    }

    /// <summary>
    /// Invalidate all caches (call after practice activities)
    /// </summary>
    public void InvalidateAll()
    {
        _vocabSummaryCache = null;
        _practiceHeatCache = null;
        _resourceProgressCache = null;
        _skillProgressCache.Clear();
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Cache INVALIDATED: All");
    }

    /// <summary>
    /// Invalidate specific cache entries
    /// </summary>
    public void InvalidateVocabSummary() => _vocabSummaryCache = null;
    public void InvalidatePracticeHeat() => _practiceHeatCache = null;
    public void InvalidateResourceProgress() => _resourceProgressCache = null;
    public void InvalidateSkillProgress(int skillId) => _skillProgressCache.Remove(skillId);

    /// <summary>
    /// Internal cache entry with expiration
    /// </summary>
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
