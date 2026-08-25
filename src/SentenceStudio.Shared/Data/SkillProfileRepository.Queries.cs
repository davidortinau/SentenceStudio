using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Skills;

namespace SentenceStudio.Data;

/// <summary>
/// The projection-only read side of the skill shelf, for callers that describe skills rather than
/// edit them.
/// </summary>
/// <remarks>
/// Archived skills are excluded from every method here, matching <see cref="ListAsync"/>'s default
/// and the skills screen. A number the learner is told about their own account has to agree with
/// the screen they can open to check it.
/// </remarks>
public partial class SkillProfileRepository
{
    public async Task<int> CountActiveSkillsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasSkillOwner(userProfileId, nameof(CountActiveSkillsAsync)))
        {
            return 0;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.SkillProfiles
            .AsNoTracking()
            .CountAsync(s => s.UserProfileId == userProfileId && !s.IsArchived, cancellationToken);
    }

    public async Task<IReadOnlyList<SkillProfileSummary>> GetRecentActiveSkillsAsync(
        string userProfileId,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!HasSkillOwner(userProfileId, nameof(GetRecentActiveSkillsAsync)) || maxResults <= 0)
        {
            return [];
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.SkillProfiles
            .AsNoTracking()
            .Where(s => s.UserProfileId == userProfileId && !s.IsArchived)
            .OrderByDescending(s => s.UpdatedAt)
            .Take(maxResults)
            .Select(s => new SkillProfileSummary(s.Id, s.Title, s.Description, s.Language))
            .ToListAsync(cancellationToken);
    }

    public async Task<SkillProfileDetailFacts?> GetActiveSkillDetailAsync(
        string userProfileId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        if (!HasSkillOwner(userProfileId, nameof(GetActiveSkillDetailAsync)) || string.IsNullOrWhiteSpace(skillId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.SkillProfiles
            .AsNoTracking()
            .Where(s => s.Id == skillId && s.UserProfileId == userProfileId && !s.IsArchived)
            .Select(s => new SkillProfileDetailFacts(s.Id, s.Title, s.Description, s.Language, s.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private bool HasSkillOwner(string userProfileId, string method)
    {
        if (!string.IsNullOrWhiteSpace(userProfileId))
        {
            return true;
        }

        _logger.LogWarning(
            "SkillProfileRepository.{Method} called without an owner — returning empty to prevent cross-tenant data leak.",
            method);
        return false;
    }
}
