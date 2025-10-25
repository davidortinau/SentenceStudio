using SentenceStudio.Data;

namespace SentenceStudio.Services.Progress;

public class ProgressService : IProgressService
{
    private readonly LearningResourceRepository _resourceRepo;
    private readonly SkillProfileRepository _skillRepo;
    private readonly UserActivityRepository _activityRepo;
    private readonly VocabularyProgressService _vocabService;
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly ProgressCacheService _cache;

    public ProgressService(
        LearningResourceRepository resourceRepo,
        SkillProfileRepository skillRepo,
        UserActivityRepository activityRepo,
        VocabularyProgressService vocabService,
        VocabularyProgressRepository progressRepo,
        ProgressCacheService cache)
    {
        _resourceRepo = resourceRepo;
        _skillRepo = skillRepo;
        _activityRepo = activityRepo;
        _vocabService = vocabService;
        _progressRepo = progressRepo;
        _cache = cache;
    }

    public async Task<List<ResourceProgress>> GetRecentResourceProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetResourceProgress();
        if (cached != null)
            return cached;

        // PHASE 1 OPTIMIZATION: Use lightweight query and SQL aggregation instead of N+1 queries
        var resources = await _resourceRepo.GetAllResourcesLightweightAsync();
        var recent = resources
            .Take(20) // Already ordered by UpdatedAt in GetAllResourcesLightweightAsync
            .ToList();

        // Get progress aggregations for all recent resources in ONE query
        var resourceIds = recent.Select(r => r.Id).ToList();
        var aggregations = await _progressRepo.GetMultipleResourceProgressAggregationsAsync(resourceIds);

        var list = new List<ResourceProgress>();
        foreach (var r in recent)
        {
            // Get aggregated data from dictionary (O(1) lookup)
            if (aggregations.TryGetValue(r.Id, out var agg))
            {
                var minutes = Math.Clamp(agg.TotalAttempts / 3, 0, 180);
                var last = r.UpdatedAt == default ? r.CreatedAt : r.UpdatedAt;
                list.Add(new ResourceProgress(
                    r.Id,
                    r.Title ?? $"Resource #{r.Id}",
                    agg.AverageMasteryScore,
                    last.ToUniversalTime(),
                    agg.TotalAttempts,
                    agg.CorrectRate,
                    minutes));
            }
            else
            {
                // Resource has no progress data yet
                var last = r.UpdatedAt == default ? r.CreatedAt : r.UpdatedAt;
                list.Add(new ResourceProgress(
                    r.Id,
                    r.Title ?? $"Resource #{r.Id}",
                    0,
                    last.ToUniversalTime(),
                    0,
                    0,
                    0));
            }
        }

        var result = list
            .OrderByDescending(x => x.LastActivityUtc)
            .Take(max)
            .ToList();

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetResourceProgress(result);
        return result;
    }

    public async Task<List<SkillProgress>> GetRecentSkillProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default)
    {
        // PHASE 1 OPTIMIZATION: Use SQL aggregation instead of loading all vocab and progress
        var skills = await _skillRepo.ListAsync();

        // Get overall proficiency in ONE query instead of loading all vocab words
        var overallAgg = await _progressRepo.GetOverallProgressAggregationAsync();
        double prof = overallAgg?.AverageMasteryScore ?? 0;

        var list = new List<SkillProgress>();
        foreach (var s in skills)
        {
            var last = s.UpdatedAt == default ? s.CreatedAt : s.UpdatedAt;
            var delta = 0.0; // Could compute from last 7d vs prior period when detailed events are available
            list.Add(new SkillProgress(s.Id, s.Title ?? $"Skill #{s.Id}", prof, delta, last.ToUniversalTime()));
        }

        return list
            .OrderByDescending(x => x.LastActivityUtc)
            .Take(max)
            .ToList();
    }

    public async Task<SkillProgress?> GetSkillProgressAsync(int skillId, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetSkillProgress(skillId);
        if (cached != null)
            return cached;

        // PHASE 1 OPTIMIZATION: Use SQL aggregation instead of loading all vocab and progress
        var skills = await _skillRepo.ListAsync();
        var s = skills.FirstOrDefault(x => x.Id == skillId);
        if (s == null) return null;

        // Get overall proficiency in ONE query
        var overallAgg = await _progressRepo.GetOverallProgressAggregationAsync();
        double prof = overallAgg?.AverageMasteryScore ?? 0;

        var last = s.UpdatedAt == default ? s.CreatedAt : s.UpdatedAt;
        var result = new SkillProgress(s.Id, s.Title ?? $"Skill #{s.Id}", prof, 0.0, last.ToUniversalTime());

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetSkillProgress(skillId, result);
        return result;
    }

    public async Task<VocabProgressSummary> GetVocabSummaryAsync(DateTime fromUtc, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetVocabSummary();
        if (cached != null)
            return cached;

        System.Diagnostics.Debug.WriteLine("🏴‍☠️ Loading VocabSummary from database");
        // PHASE 1 OPTIMIZATION: Use SQL aggregation instead of loading all progress records
        var (newCount, learning, review, known) = await _progressRepo.GetVocabSummaryCountsAsync();
        var success7d = await _progressRepo.GetSuccessRate7dAsync();

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ VocabSummary: New={newCount}, Learning={learning}, Review={review}, Known={known}, Success7d={success7d}");
        var result = new VocabProgressSummary(newCount, learning, review, known, success7d);

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetVocabSummary(result);
        return result;
    }

    public async Task<IReadOnlyList<PracticeHeatPoint>> GetPracticeHeatAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // PHASE 2 OPTIMIZATION: Check cache first
        var cached = _cache.GetPracticeHeat();
        if (cached != null)
            return cached;

        // Use UserActivity as the single source of truth for all practice activities
        var userActivities = await _activityRepo.GetByDateRangeAsync(fromUtc, toUtc);

        // Group by day
        var dailyActivities = userActivities
            .GroupBy(a => a.CreatedAt.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        // Generate all days in the range
        var days = Enumerable.Range(0, (int)(toUtc.Date - fromUtc.Date).TotalDays + 1)
            .Select(i => fromUtc.Date.AddDays(i))
            .ToList();

        var results = new List<PracticeHeatPoint>();
        foreach (var day in days)
        {
            // Get activity count for this day
            int count = dailyActivities.GetValueOrDefault(day.Date, 0);
            results.Add(new PracticeHeatPoint(day, count));
        }

        // PHASE 2 OPTIMIZATION: Cache the result
        _cache.SetPracticeHeat(results);
        return results;
    }
}
