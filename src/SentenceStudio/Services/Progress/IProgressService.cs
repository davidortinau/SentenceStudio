using System.Collections.ObjectModel;

namespace SentenceStudio.Services.Progress;

// Lightweight DTOs for dashboard progress visuals
public record ResourceProgress
(
    int ResourceId,
    string Title,
    double Proficiency, // 0..1
    DateTime LastActivityUtc,
    int Attempts,
    double CorrectRate, // 0..1
    int Minutes
);

public record SkillProgress
(
    int SkillId,
    string Title,
    double Proficiency, // 0..1
    double Delta7d,     // -1..+1
    DateTime LastActivityUtc
);

public record VocabProgressSummary
(
    int New,
    int Learning,
    int Review,
    int Known,
    double SuccessRate7d // 0..1
);

public record PracticeHeatPoint(DateTime Date, int Count);

public interface IProgressService
{
    Task<List<ResourceProgress>> GetRecentResourceProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default);
    Task<List<SkillProgress>> GetRecentSkillProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default);
    Task<SkillProgress?> GetSkillProgressAsync(int skillId, CancellationToken ct = default);
    Task<VocabProgressSummary> GetVocabSummaryAsync(DateTime fromUtc, CancellationToken ct = default);
    Task<IReadOnlyList<PracticeHeatPoint>> GetPracticeHeatAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
