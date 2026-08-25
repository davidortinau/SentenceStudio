using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Resources;

namespace SentenceStudio.Data;

/// <summary>
/// The metadata-only read side of the resource shelf.
/// </summary>
/// <remarks>
/// <para>
/// The entity-returning methods on this repository load whole <c>LearningResource</c> rows, which
/// is right for a screen that is about to render or edit one. It is wrong for anything that only
/// needs to describe the shelf: <c>Transcript</c> and <c>Translation</c> are the two largest
/// columns on the row, and pulling them to answer "does this have a transcript?" costs the whole
/// document to produce one boolean.
/// </para>
/// <para>
/// So these queries project. That is not only a performance choice — a transcript that was never
/// selected cannot be handed to a language model by a later mistake, which is the guarantee the
/// coach read tools depend on.
/// </para>
/// </remarks>
public partial class LearningResourceRepository
{
    public async Task<int> CountResourcesAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasResourceOwner(userProfileId, nameof(CountResourcesAsync)))
        {
            return 0;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.LearningResources
            .AsNoTracking()
            .CountAsync(r => r.UserProfileId == userProfileId, cancellationToken);
    }

    public async Task<IReadOnlyList<LearningResourceSummary>> GetResourceSummariesAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasResourceOwner(userProfileId, nameof(GetResourceSummariesAsync)))
        {
            return [];
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await ProjectSummaries(db, db.LearningResources
                .AsNoTracking()
                .Where(r => r.UserProfileId == userProfileId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LearningResourceSummary>> GetRecentResourceSummariesAsync(
        string userProfileId,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!HasResourceOwner(userProfileId, nameof(GetRecentResourceSummariesAsync)) || maxResults <= 0)
        {
            return [];
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await ProjectSummaries(db, db.LearningResources
                .AsNoTracking()
                .Where(r => r.UserProfileId == userProfileId)
                .OrderByDescending(r => r.UpdatedAt)
                .Take(maxResults))
            .ToListAsync(cancellationToken);
    }

    public async Task<LearningResourceSummary?> GetResourceSummaryAsync(
        string userProfileId,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!HasResourceOwner(userProfileId, nameof(GetResourceSummaryAsync)) || string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await ProjectSummaries(db, db.LearningResources
                .AsNoTracking()
                .Where(r => r.Id == resourceId && r.UserProfileId == userProfileId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The one projection every summary query shares, so "what a resource summary contains" is
    /// decided once instead of once per caller.
    /// </summary>
    private static IQueryable<LearningResourceSummary> ProjectSummaries(
        ApplicationDbContext db,
        IQueryable<LearningResource> resources) =>
        resources.Select(r => new LearningResourceSummary(
            r.Id,
            r.Title,
            r.MediaType,
            r.Language,
            r.MediaUrl,
            r.Transcript != null && r.Transcript != string.Empty,
            r.Tags,
            r.IsSmartResource,
            db.ResourceVocabularyMappings.Count(m => m.ResourceId == r.Id)));

    private bool HasResourceOwner(string userProfileId, string method)
    {
        if (!string.IsNullOrWhiteSpace(userProfileId))
        {
            return true;
        }

        _logger.LogWarning(
            "LearningResourceRepository.{Method} called without an owner — returning empty to prevent cross-tenant data leak.",
            method);
        return false;
    }
}
