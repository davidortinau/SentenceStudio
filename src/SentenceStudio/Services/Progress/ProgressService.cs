using SentenceStudio.Data;

namespace SentenceStudio.Services.Progress;

public class ProgressService : IProgressService
{
    private readonly LearningResourceRepository _resourceRepo;
    private readonly SkillProfileRepository _skillRepo;
    private readonly UserActivityRepository _activityRepo;
    private readonly VocabularyProgressService _vocabService;

    public ProgressService(
        LearningResourceRepository resourceRepo,
        SkillProfileRepository skillRepo,
        UserActivityRepository activityRepo,
        VocabularyProgressService vocabService)
    {
        _resourceRepo = resourceRepo;
        _skillRepo = skillRepo;
        _activityRepo = activityRepo;
        _vocabService = vocabService;
    }

    public async Task<List<ResourceProgress>> GetRecentResourceProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default)
    {
        // Placeholder heuristic: use learning resources with any vocabulary and synthesize progress from vocab mastery
        var resources = await _resourceRepo.GetAllResourcesAsync();
        var recent = resources
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.CreatedAt)
            .Take(20)
            .ToList();

        var list = new List<ResourceProgress>();
        foreach (var r in recent)
        {
            // Aggregate vocab mastery for the resource
            double proficiency = 0;
            int attempts = 0;
            double correctRate = 0;
            int known = 0;
            if (r.Vocabulary?.Count > 0)
            {
                var progresses = new List<VocabularyProgress>();
                foreach (var w in r.Vocabulary)
                {
                    var p = await _vocabService.GetProgressAsync(w.Id);
                    if (p != null) progresses.Add(p);
                }

                if (progresses.Count > 0)
                {
                    proficiency = progresses.Average(p => p.MasteryScore);
                    attempts = progresses.Sum(p => p.TotalAttempts);
                    var totalCorr = progresses.Sum(p => p.CorrectAttempts);
                    var totalAtt = Math.Max(1, progresses.Sum(p => p.TotalAttempts));
                    correctRate = (double)totalCorr / totalAtt;
                    known = progresses.Count(p => p.IsKnown);
                }
            }

            var minutes = Math.Clamp(attempts / 3, 0, 180);
            var last = r.UpdatedAt == default ? r.CreatedAt : r.UpdatedAt;
            list.Add(new ResourceProgress(r.Id, r.Title ?? $"Resource #{r.Id}", proficiency, last.ToUniversalTime(), attempts, correctRate, minutes));
        }

        return list
            .OrderByDescending(x => x.LastActivityUtc)
            .Take(max)
            .ToList();
    }

    public async Task<List<SkillProgress>> GetRecentSkillProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default)
    {
        var skills = await _skillRepo.ListAsync();
        var list = new List<SkillProgress>();
        foreach (var s in skills)
        {
            // Placeholder: derive proficiency from average of all vocab mastery; delta random-ish based on recentness
            var allVocab = await _resourceRepo.GetAllVocabularyWordsAsync();
            double prof = 0;
            if (allVocab.Count > 0)
            {
                var progresses = new List<VocabularyProgress>();
                foreach (var w in allVocab)
                {
                    var p = await _vocabService.GetProgressAsync(w.Id);
                    if (p != null) progresses.Add(p);
                }
                prof = progresses.Count > 0 ? progresses.Average(p => p.MasteryScore) : 0;
            }

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
        var skills = await _skillRepo.ListAsync();
        var s = skills.FirstOrDefault(x => x.Id == skillId);
        if (s == null) return null;

        var allVocab = await _resourceRepo.GetAllVocabularyWordsAsync();
        double prof = 0;
        if (allVocab.Count > 0)
        {
            var progresses = new List<VocabularyProgress>();
            foreach (var w in allVocab)
            {
                var p = await _vocabService.GetProgressAsync(w.Id);
                if (p != null) progresses.Add(p);
            }
            prof = progresses.Count > 0 ? progresses.Average(p => p.MasteryScore) : 0;
        }

        var last = s.UpdatedAt == default ? s.CreatedAt : s.UpdatedAt;
        return new SkillProgress(s.Id, s.Title ?? $"Skill #{s.Id}", prof, 0.0, last.ToUniversalTime());
    }

    public async Task<VocabProgressSummary> GetVocabSummaryAsync(DateTime fromUtc, CancellationToken ct = default)
    {
        var all = await _vocabService.GetAllProgressAsync();
        int known = all.Count(p => p.IsKnown);
        int review = all.Count(p => p.IsDueForReview && !p.IsKnown);
        int learning = all.Count(p => !p.IsKnown && !p.IsDueForReview && p.TotalAttempts > 0);
        int @new = all.Count(p => p.TotalAttempts == 0);

        // Very rough success rate over last week using available aggregates if detailed attempts aren’t available
        double success7d = 0;
        var recent = all.Where(p => (DateTime.Now - p.LastPracticedAt).TotalDays <= 7).ToList();
        if (recent.Count > 0)
        {
            var corr = recent.Sum(p => p.CorrectAttempts);
            var att = Math.Max(1, recent.Sum(p => p.TotalAttempts));
            success7d = (double)corr / att;
        }

        return new VocabProgressSummary(@new, learning, review, known, success7d);
    }

    public async Task<IReadOnlyList<PracticeHeatPoint>> GetPracticeHeatAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var all = await _vocabService.GetAllProgressAsync();
        var days = Enumerable.Range(0, (int)(toUtc.Date - fromUtc.Date).TotalDays + 1)
            .Select(i => fromUtc.Date.AddDays(i))
            .ToList();

        var results = new List<PracticeHeatPoint>();
        foreach (var day in days)
        {
            // Approximate: count words practiced that day based on LastPracticedAt rounded
            int count = all.Count(p => p.LastPracticedAt.Date == day.Date);
            results.Add(new PracticeHeatPoint(day, count));
        }

        return results;
    }
}
