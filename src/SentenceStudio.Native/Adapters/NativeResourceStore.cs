using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

public sealed class NativeResourceStore(
    IServiceScopeFactory scopes,
    LearningResourceRepository resources,
    ILogger<NativeResourceStore> logger) : INativeResourceStore
{
    public async Task<IReadOnlyList<NativeResourceItem>> ListAsync(
        string owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            logger.LogWarning("Native resource list refused an absent owner.");
            return [];
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await ListQuery(db, owner).ToListAsync(cancellationToken);

        return LearningResourceRepository.OrderResourcesSmartFirst(rows)
            .Select(resource => new NativeResourceItem(
                resource.Id, resource.Title ?? string.Empty,
                resource.Description, resource.Tags, resource.MediaType,
                resource.Language, resource.IsSmartResource,
                resource.CreatedAt, resource.UpdatedAt))
            .ToArray();
    }

    internal static IQueryable<LearningResource> ListQuery(
        ApplicationDbContext db, string owner) =>
        db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner)
            .Select(resource => new LearningResource
            {
                Id = resource.Id,
                Title = resource.Title,
                Description = resource.Description,
                Tags = resource.Tags,
                MediaType = resource.MediaType,
                Language = resource.Language,
                IsSmartResource = resource.IsSmartResource,
                CreatedAt = resource.CreatedAt,
                UpdatedAt = resource.UpdatedAt
            });

    public async Task<NativeResourceDetail?> GetAsync(
        string owner, string resourceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(resourceId))
        {
            logger.LogWarning("Native resource detail refused an absent owner or resource.");
            return null;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await DetailQuery(db, owner, resourceId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal static IQueryable<NativeResourceDetail> DetailQuery(
        ApplicationDbContext db, string owner, string resourceId) =>
        db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner && resource.Id == resourceId)
            .Select(resource => new NativeResourceDetail(
                resource.Id,
                resource.Title ?? string.Empty,
                resource.Description,
                resource.Tags,
                resource.MediaType,
                resource.Language,
                resource.MediaUrl,
                resource.IsSmartResource,
                resource.CreatedAt,
                resource.UpdatedAt,
                resource.Transcript != null && resource.Transcript != "",
                resource.Transcript == null ? null
                    : resource.Transcript.Length > 240
                        ? resource.Transcript.Substring(0, 240) : resource.Transcript,
                resource.Translation != null && resource.Translation != "",
                resource.Translation == null ? null
                    : resource.Translation.Length > 240
                        ? resource.Translation.Substring(0, 240) : resource.Translation,
                db.ResourceVocabularyMappings.Count(mapping =>
                    mapping.ResourceId == resource.Id),
                new ResourceMetadataSnapshot(resource.UpdatedAt, resource.Title,
                    resource.Description, resource.Tags,
                    db.Database.IsSqlite() ? resource.UpdatedAt.ToString() : null)));

    public Task<ResourceMetadataUpdateResult> SaveMetadataAsync(
        string owner, string resourceId, ResourceMetadataSnapshot original,
        string title, string? description, string? tags,
        CancellationToken cancellationToken) =>
        resources.UpdateResourceMetadataAsync(
            resourceId, owner, original, title, description, tags,
            cancellationToken);
}
