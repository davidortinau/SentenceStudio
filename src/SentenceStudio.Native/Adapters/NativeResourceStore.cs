using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

public sealed partial class NativeResourceStore(
    IServiceScopeFactory scopes,
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    NativePendingWrites pendingWrites,
    ILogger<NativeResourceStore> logger) : INativeResourceStore
{
    private bool IsActiveOwner(string owner)
    {
        var activeOwner = !string.IsNullOrWhiteSpace(owner)
            && userScope.TryGetUserProfileId(out var active)
            && string.Equals(active, owner, StringComparison.Ordinal)
            && string.Equals(
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            owner, StringComparison.Ordinal);
        if (!activeOwner)
            logger.LogWarning("Native resource operation refused a missing or changed active owner.");
        return activeOwner;
    }

    private async Task<TResult> WithResourcesAsync<TResult>(
        Func<LearningResourceRepository, Task<TResult>> operation)
    {
        using var scope = scopes.CreateScope();
        return await operation(
            scope.ServiceProvider.GetRequiredService<LearningResourceRepository>());
    }

    public async Task<IReadOnlyList<NativeResourceItem>> ListAsync(
        string owner, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
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
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(resourceId))
        {
            logger.LogWarning("Native resource detail refused an absent owner or resource.");
            return null;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var detail = await DetailQuery(db, owner, resourceId)
            .SingleOrDefaultAsync(cancellationToken);
        if (detail is null)
            return null;

        var words = await db.ResourceVocabularyMappings.AsNoTracking()
            .Where(mapping => mapping.ResourceId == resourceId
                && db.LearningResources.Any(resource =>
                    resource.Id == mapping.ResourceId && resource.UserProfileId == owner))
            .Join(db.VocabularyWords.AsNoTracking(),
                mapping => mapping.VocabularyWordId, word => word.Id,
                (_, word) => word)
            .OrderBy(word => word.TargetLanguageTerm)
            .ThenBy(word => word.Id)
            .Select(word => new NativeResourceVocabularyRow(
                    word.Id, word.TargetLanguageTerm ?? string.Empty,
                    word.NativeLanguageTerm ?? string.Empty, word.LexicalUnitType,
                    word.Tags, word.Language, word.Lemma, word.PartOfSpeech))
            .ToListAsync(cancellationToken);
        return detail with {
            Vocabulary = words.ToImmutableArray(),
            VocabularyCount = words.Count,
            OriginalContent = new ResourceContentSnapshot(
                detail.OriginalMetadata, detail.MediaType, detail.Language,
                detail.MediaUrl, detail.Transcript, detail.Translation)
        };
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
                    db.Database.IsSqlite() ? resource.UpdatedAt.ToString() : null))
            {
                Transcript = resource.Transcript,
                Translation = resource.Translation
            });

    public Task<ResourceMetadataUpdateResult> SaveMetadataAsync(
        string owner, string resourceId, ResourceMetadataSnapshot original,
        string title, string? description, string? tags,
        CancellationToken cancellationToken) =>
        IsActiveOwner(owner)
            ? WithResourcesAsync(repository => repository.UpdateResourceMetadataAsync(
                resourceId, owner, original, title, description, tags,
                cancellationToken))
            : Task.FromResult(ResourceMetadataUpdateResult.NotFoundOrForbidden);

    public Task<ResourceMetadataUpdateResult> SaveContentAsync(
        string owner, string resourceId, ResourceContentSnapshot original,
        ResourceContentUpdate content, CancellationToken cancellationToken) =>
        IsActiveOwner(owner)
            ? WithResourcesAsync(repository => repository.UpdateResourceContentAsync(
                resourceId, owner, original, content, cancellationToken))
            : Task.FromResult(ResourceMetadataUpdateResult.NotFoundOrForbidden);

    public async Task<NativeResourceCreateResult> CreateAsync(
        string owner, NativeResourceDraft draft, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return new(NativeResourceMutationResult.NotFoundOrForbidden);
        if (draft is null || string.IsNullOrWhiteSpace(draft.Title))
            return new(NativeResourceMutationResult.Invalid, ErrorKey: "ResourceAdd_TitleRequired");

        var resource = new LearningResource
        {
            Title = draft.Title,
            Description = draft.Description,
            Tags = draft.Tags,
            MediaType = string.IsNullOrWhiteSpace(draft.MediaType) ? "Other" : draft.MediaType,
            Language = string.IsNullOrWhiteSpace(draft.Language) ? "Other" : draft.Language,
            MediaUrl = draft.MediaUrl,
            Transcript = draft.Transcript,
            Translation = draft.Translation
        };
        var staged = draft.StagedVocabulary.IsDefault
            ? [] : draft.StagedVocabulary;
        var words = staged.Select(row => new VocabularyWord
        {
            Id = row.Id,
            TargetLanguageTerm = row.TargetTerm.Trim(),
            NativeLanguageTerm = row.NativeTerm.Trim(),
            Language = row.Language ?? resource.Language,
            Tags = row.Tags,
            Lemma = row.Lemma,
            PartOfSpeech = row.PartOfSpeech,
            LexicalUnitType = row.LexicalUnitType == LexicalUnitType.Unknown
                ? VocabularyClassificationBackfillService.ClassifyHeuristic(row.TargetTerm, row.Tags)
                : row.LexicalUnitType
        }).ToArray();
        var outcome = await WithResourcesAsync(repository =>
            repository.CreateOwnedResourceWithVocabularyAsync(
                resource, words, owner, cancellationToken));
        return outcome.Status switch
        {
            ResourceCreationStatus.Success => new(NativeResourceMutationResult.Success,
                outcome.ResourceId),
            ResourceCreationStatus.Invalid => new(NativeResourceMutationResult.Invalid,
                ErrorKey: "NativeResources_SaveInvalid"),
            ResourceCreationStatus.NotFoundOrForbidden =>
                new(NativeResourceMutationResult.NotFoundOrForbidden),
            ResourceCreationStatus.AlreadyExists =>
                new(NativeResourceMutationResult.AlreadyPresent),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}
