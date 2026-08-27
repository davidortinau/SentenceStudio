namespace SentenceStudio.Application.Skills;

/// <summary>One skill profile, as much of it as a list needs.</summary>
public sealed record SkillProfileSummary(
    string SkillId,
    string? Title,
    string? Description,
    string? Language);

/// <summary>One skill profile, with the creation stamp a detail view needs.</summary>
public sealed record SkillProfileDetailFacts(
    string SkillId,
    string? Title,
    string? Description,
    string? Language,
    DateTime CreatedAt);

/// <summary>
/// Reads a learner's skill profiles.
/// </summary>
/// <remarks>
/// Every method here excludes archived skills, for the same reason the skills screen does: an
/// archived skill is one the learner has put away, so a count or a list that includes it describes
/// a shelf they cannot see. A caller that genuinely manages the archive asks the repository for it
/// explicitly.
/// </remarks>
public interface ISkillProfileQueries
{
    /// <summary>Counts the learner's unarchived skills. An empty identifier counts zero.</summary>
    Task<int> CountActiveSkillsAsync(string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the learner's unarchived skills, most recently updated first, capped at
    /// <paramref name="maxResults"/>. An empty identifier returns nothing.
    /// </summary>
    Task<IReadOnlyList<SkillProfileSummary>> GetRecentActiveSkillsAsync(
        string userProfileId,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one unarchived skill the learner owns, or <c>null</c> when the skill is missing,
    /// archived, or belongs to someone else. An empty identifier returns <c>null</c>.
    /// </summary>
    Task<SkillProfileDetailFacts?> GetActiveSkillDetailAsync(
        string userProfileId,
        string skillId,
        CancellationToken cancellationToken = default);
}
