using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

public sealed partial class NativeResourceStore
{
    public async Task<IReadOnlyList<NativeResourceVocabularyRow>> SearchVocabularyAsync(
        string owner, string resourceId, string language, string query,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return [];
        var matches = await WithResourcesAsync(repository =>
            repository.SearchOwnedVocabularyForDraftAsync(
                query, language, resourceId, owner, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return matches.Select(word => new NativeResourceVocabularyRow(
            word.Id, word.TargetLanguageTerm ?? string.Empty,
            word.NativeLanguageTerm ?? string.Empty, word.LexicalUnitType,
            word.Tags, word.Language, word.Lemma, word.PartOfSpeech)
        {
            IsExisting = true
        }).ToArray();
    }

    public async Task<NativeResourceMutationResult> AttachVocabularyAsync(
        string owner, string resourceId, string wordId, string language,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return NativeResourceMutationResult.NotFoundOrForbidden;
        return Map(await WithResourcesAsync(repository =>
            repository.AttachOwnedVocabularyAsync(
                resourceId, wordId, language, owner, cancellationToken)));
    }

    public async Task<NativeResourceMutationResult> CreateVocabularyAsync(
        string owner, string resourceId, NativeResourceVocabularyRow word, string language,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return NativeResourceMutationResult.NotFoundOrForbidden;
        return Map(await WithResourcesAsync(repository =>
            repository.CreateAndAttachOwnedVocabularyAsync(resourceId,
                ToWord(word), language, owner, cancellationToken)));
    }

    public async Task<NativeResourceImportResult> ImportVocabularyAsync(
        string owner, string resourceId, string? language,
        IReadOnlyList<NativeResourceVocabularyRow> words,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
        if (words is null)
            return new(0, 0, NativeResourceMutationResult.Invalid);
        var outcome = await WithResourcesAsync(repository =>
            repository.ImportOwnedVocabularyAsync(resourceId,
                words.Select(ToWord).ToArray(), language, owner, cancellationToken));
        return new(outcome.Added, outcome.Skipped, Map(outcome.Status))
        {
            CreatedCount = outcome.CreatedCount,
            ExistingLinkedCount = outcome.ExistingLinkedCount
        };
    }

    public async Task<NativeResourceMutationResult> RemoveVocabularyAsync(
        string owner, string resourceId, string wordId, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return NativeResourceMutationResult.NotFoundOrForbidden;
        return Map(await WithResourcesAsync(repository =>
            repository.RemoveOwnedVocabularyAsync(
                resourceId, wordId, owner, cancellationToken)));
    }

    public async Task<NativeResourceMutationResult> DeleteAsync(
        string owner, string resourceId, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(resourceId))
            return NativeResourceMutationResult.NotFoundOrForbidden;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var smart = await db.LearningResources.AsNoTracking()
            .Where(resource => resource.Id == resourceId && resource.UserProfileId == owner)
            .Select(resource => (bool?)resource.IsSmartResource)
            .SingleOrDefaultAsync(cancellationToken);
        if (smart is null)
            return NativeResourceMutationResult.NotFoundOrForbidden;
        if (smart.Value)
            return NativeResourceMutationResult.SmartReadOnly;
        cancellationToken.ThrowIfCancellationRequested();
        var affected = await scope.ServiceProvider.GetRequiredService<LearningResourceRepository>()
            .DeleteResourceAsync(
            new LearningResource { Id = resourceId }, owner,
            allowSmartResource: false, triggerSync: false);
        return affected > 0
            ? NativeResourceMutationResult.Success
            : affected == 0
                ? NativeResourceMutationResult.NotFoundOrForbidden
                : NativeResourceMutationResult.Failed;
    }

    private static VocabularyWord ToWord(NativeResourceVocabularyRow row) =>
        new()
        {
            Id = row.Id,
            TargetLanguageTerm = row.TargetTerm,
            NativeLanguageTerm = row.NativeTerm,
            LexicalUnitType = row.LexicalUnitType,
            Language = row.Language,
            Lemma = row.Lemma,
            Tags = row.Tags,
            PartOfSpeech = row.PartOfSpeech
        };

    private static NativeResourceMutationResult Map(ResourceVocabularyWriteStatus result) =>
        result switch
        {
            ResourceVocabularyWriteStatus.Success => NativeResourceMutationResult.Success,
            ResourceVocabularyWriteStatus.NotFoundOrForbidden =>
                NativeResourceMutationResult.NotFoundOrForbidden,
            ResourceVocabularyWriteStatus.SmartReadOnly => NativeResourceMutationResult.SmartReadOnly,
            ResourceVocabularyWriteStatus.Invalid => NativeResourceMutationResult.Invalid,
            ResourceVocabularyWriteStatus.AlreadyPresent =>
                NativeResourceMutationResult.AlreadyPresent,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
        };
}
