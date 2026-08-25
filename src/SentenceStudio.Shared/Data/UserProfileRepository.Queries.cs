using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Learners;

namespace SentenceStudio.Data;

/// <summary>
/// The settings-only read side of a learner's profile.
/// </summary>
/// <remarks>
/// <see cref="GetByIdAsync"/> returns the whole <c>UserProfile</c> row, which is what an editor
/// needs and more than a reader should ever hold. The row carries the learner's name, their email
/// address, and their provider API keys alongside their language preferences, so a caller that
/// only wants the preferences and happens to be careful is one refactor away from not being
/// careful. Projecting in SQL removes the possibility rather than relying on discipline.
/// </remarks>
public partial class UserProfileRepository
{
    public async Task<LearnerProfileFacts?> GetProfileFactsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning(
                "UserProfileRepository.GetProfileFactsAsync called without an owner — returning null to prevent cross-tenant data leak.");
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.UserProfiles
            .AsNoTracking()
            .Where(p => p.Id == userProfileId)
            .Select(p => new LearnerProfileFacts(
                p.TargetLanguage,
                p.TargetLanguages,
                p.NativeLanguage,
                p.DisplayLanguage,
                p.PreferredSessionMinutes,
                p.TargetCEFRLevel,
                p.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
