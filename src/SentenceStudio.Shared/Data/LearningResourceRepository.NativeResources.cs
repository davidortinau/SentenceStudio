using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

public partial class LearningResourceRepository
{
    public async Task<List<VocabularyWord>> SearchOwnedVocabularyForDraftAsync(
        string query, string language, string resourceId, string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning("Draft vocabulary search refused an absent owner.");
            return [];
        }
        if (string.IsNullOrWhiteSpace(language)
            || language.Equals("Other", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(resourceId))
            return [];
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // A changed form language is not persisted until Save, but only the
        // same owner's editable resource may search words in that draft language.
        if (!await db.LearningResources.AnyAsync(resource =>
                resource.Id == resourceId && resource.UserProfileId == userProfileId
                && !resource.IsSmartResource, cancellationToken))
            return [];

        var ownerWords = db.ResourceVocabularyMappings
            .Join(db.LearningResources.Where(resource =>
                    resource.UserProfileId == userProfileId),
                mapping => mapping.ResourceId, resource => resource.Id,
                (mapping, _) => mapping.VocabularyWordId);
        var attached = db.ResourceVocabularyMappings
            .Where(mapping => mapping.ResourceId == resourceId)
            .Select(mapping => mapping.VocabularyWordId);
        var search = query?.ToLower().Trim() ?? string.Empty;
        var words = db.VocabularyWords.AsNoTracking()
            .Where(word => ownerWords.Contains(word.Id)
                && !attached.Contains(word.Id) && word.Language == language);
        if (search.Length > 0)
            words = words.Where(word =>
                (word.TargetLanguageTerm != null
                    && word.TargetLanguageTerm.ToLower().Contains(search))
                || (word.NativeLanguageTerm != null
                    && word.NativeLanguageTerm.ToLower().Contains(search)));
        return await words
            .OrderBy(word => word.TargetLanguageTerm == null
                || !word.TargetLanguageTerm.ToLower().StartsWith(search))
            .ThenBy(word => word.TargetLanguageTerm)
            .Take(20)
            .ToListAsync(cancellationToken);
    }

    public async Task<ResourceVocabularyWriteStatus> AttachOwnedVocabularyAsync(
        string resourceId, string wordId, string language, string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning("AttachOwnedVocabularyAsync refused an absent owner.");
            return ResourceVocabularyWriteStatus.NotFoundOrForbidden;
        }
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (resource, refusal) = await EditableResourceAsync(
            db, userProfileId, resourceId, language, cancellationToken);
        if (resource is null)
            return refusal;

        var word = await db.VocabularyWords.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == wordId, cancellationToken);
        if (word is null || word.Language != language
            || !await IsVocabularyWordAvailableToUserAsync(db, wordId, userProfileId))
            return ResourceVocabularyWriteStatus.NotFoundOrForbidden;
        if (await db.ResourceVocabularyMappings.AnyAsync(mapping =>
                mapping.ResourceId == resourceId && mapping.VocabularyWordId == wordId,
                cancellationToken))
            return ResourceVocabularyWriteStatus.AlreadyPresent;

        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            ResourceId = resourceId, VocabularyWordId = wordId
        });
        await db.SaveChangesAsync(cancellationToken);
        return ResourceVocabularyWriteStatus.Success;
    }

    public async Task<ResourceVocabularyWriteStatus> CreateAndAttachOwnedVocabularyAsync(
        string resourceId, VocabularyWord word, string language, string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning("CreateAndAttachOwnedVocabularyAsync refused an absent owner.");
            return ResourceVocabularyWriteStatus.NotFoundOrForbidden;
        }
        if (word is null || string.IsNullOrWhiteSpace(word.TargetLanguageTerm))
            return ResourceVocabularyWriteStatus.Invalid;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (resource, refusal) = await EditableResourceAsync(
            db, userProfileId, resourceId, language, cancellationToken);
        if (resource is null)
            return refusal;

        var targetTerm = word.TargetLanguageTerm.Trim();
        if (await db.ResourceVocabularyMappings
            .Where(mapping => mapping.ResourceId == resourceId)
            .Join(db.VocabularyWords, mapping => mapping.VocabularyWordId,
                item => item.Id, (_, item) => item.TargetLanguageTerm)
            .AnyAsync(term => term != null && term.ToLower() == targetTerm.ToLower(),
                cancellationToken))
            return ResourceVocabularyWriteStatus.AlreadyPresent;

        var reusable = await FindReusableWordAsync(db, userProfileId, targetTerm,
            language, cancellationToken);
        if (reusable is null)
        {
            word.Id = Guid.NewGuid().ToString();
            word.UserProfileId = userProfileId;
            word.TargetLanguageTerm = targetTerm;
            word.NativeLanguageTerm = word.NativeLanguageTerm?.Trim() ?? string.Empty;
            word.Language = language;
            word.CreatedAt = DateTime.UtcNow;
            word.UpdatedAt = word.CreatedAt;
            if (word.LexicalUnitType == LexicalUnitType.Unknown)
                word.LexicalUnitType = VocabularyClassificationBackfillService
                    .ClassifyHeuristic(targetTerm, word.Tags);
            db.VocabularyWords.Add(word);
            reusable = word;
        }

        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            ResourceId = resourceId, VocabularyWordId = reusable.Id
        });
        await db.SaveChangesAsync(cancellationToken);
        return ResourceVocabularyWriteStatus.Success;
    }

    public async Task<ResourceVocabularyImportResult> ImportOwnedVocabularyAsync(
        string resourceId, IReadOnlyList<VocabularyWord> words,
        string? language, string userProfileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning("ImportOwnedVocabularyAsync refused an absent owner.");
            return new(ResourceVocabularyWriteStatus.NotFoundOrForbidden);
        }
        if (words is null)
            return new(ResourceVocabularyWriteStatus.Invalid);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (resource, refusal) = await EditableResourceAsync(
            db, userProfileId, resourceId, language, cancellationToken,
            allowOtherLanguage: true);
        if (resource is null)
            return new(refusal);

        var attached = await db.ResourceVocabularyMappings
            .Where(mapping => mapping.ResourceId == resourceId)
            .Join(db.VocabularyWords, mapping => mapping.VocabularyWordId,
                word => word.Id, (_, word) => word.TargetLanguageTerm)
            .ToListAsync(cancellationToken);
        var seen = attached.Where(term => term is not null)
            .Select(term => term!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;
        var created = 0;
        var existingLinked = 0;
        foreach (var candidate in words)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = candidate.TargetLanguageTerm?.Trim();
            if (string.IsNullOrWhiteSpace(target) || !seen.Add(target))
            {
                skipped++;
                continue;
            }
            var wordLanguage = string.IsNullOrWhiteSpace(candidate.Language)
                ? language : candidate.Language;
            var reusable = await FindReusableWordAsync(db, userProfileId, target,
                wordLanguage, cancellationToken);
            if (reusable is null)
            {
                candidate.Id = Guid.NewGuid().ToString();
                candidate.UserProfileId = userProfileId;
                candidate.TargetLanguageTerm = target;
                candidate.NativeLanguageTerm = candidate.NativeLanguageTerm?.Trim()
                    ?? string.Empty;
                candidate.Language = wordLanguage;
                candidate.CreatedAt = DateTime.UtcNow;
                candidate.UpdatedAt = candidate.CreatedAt;
                if (candidate.LexicalUnitType == LexicalUnitType.Unknown)
                    candidate.LexicalUnitType = VocabularyClassificationBackfillService
                        .ClassifyHeuristic(target, candidate.Tags);
                db.VocabularyWords.Add(candidate);
                reusable = candidate;
                created++;
            }
            else
                existingLinked++;
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                ResourceId = resourceId,
                VocabularyWordId = reusable.Id
            });
            added++;
        }
        if (added != 0)
            await db.SaveChangesAsync(cancellationToken);
        return new(ResourceVocabularyWriteStatus.Success, added, skipped)
        {
            CreatedCount = created,
            ExistingLinkedCount = existingLinked
        };
    }

    public async Task<ResourceVocabularyWriteStatus> RemoveOwnedVocabularyAsync(
        string resourceId, string wordId, string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning("RemoveOwnedVocabularyAsync refused an absent owner.");
            return ResourceVocabularyWriteStatus.NotFoundOrForbidden;
        }
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (resource, refusal) = await EditableResourceAsync(
            db, userProfileId, resourceId, null, cancellationToken);
        if (resource is null)
            return refusal;

        var mapping = await db.ResourceVocabularyMappings.SingleOrDefaultAsync(
            row => row.ResourceId == resourceId && row.VocabularyWordId == wordId,
            cancellationToken);
        if (mapping is null)
            return ResourceVocabularyWriteStatus.NotFoundOrForbidden;
        db.ResourceVocabularyMappings.Remove(mapping);
        await db.SaveChangesAsync(cancellationToken);
        return ResourceVocabularyWriteStatus.Success;
    }

    private static async Task<(LearningResource?, ResourceVocabularyWriteStatus)> EditableResourceAsync(
        ApplicationDbContext db, string owner, string resourceId, string? language,
        CancellationToken cancellationToken, bool allowOtherLanguage = false)
    {
        if (string.IsNullOrWhiteSpace(resourceId)
            || language is not null && (string.IsNullOrWhiteSpace(language)
                || !allowOtherLanguage
                    && language.Equals("Other", StringComparison.OrdinalIgnoreCase)))
            return (null, ResourceVocabularyWriteStatus.Invalid);
        var resource = await db.LearningResources.SingleOrDefaultAsync(
            row => row.Id == resourceId && row.UserProfileId == owner, cancellationToken);
        if (resource is null)
            return (null, ResourceVocabularyWriteStatus.NotFoundOrForbidden);
        if (resource.IsSmartResource)
            return (null, ResourceVocabularyWriteStatus.SmartReadOnly);
        if (language is not null && resource.Language != language)
            return (null, ResourceVocabularyWriteStatus.Invalid);
        return (resource, ResourceVocabularyWriteStatus.Success);
    }

    // Reuse only a word available to this learner (VocabularyWordOwnership.AvailableTo):
    // never another account's word or an ownerless one carrying someone's memory aids.
    private static Task<VocabularyWord?> FindReusableWordAsync(
        ApplicationDbContext db, string owner, string targetTerm, string? language,
        CancellationToken cancellationToken) =>
        VocabularyWordOwnership.AvailableTo(db, owner)
            .Where(word => word.TargetLanguageTerm == targetTerm && word.Language == language)
            .OrderBy(word => word.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
