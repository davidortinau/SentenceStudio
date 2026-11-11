using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// The Vocabulary list and word editor. Only the owner's own words are shown, opened, saved
/// or deleted (Captain 2026-11-06: "every word should have an owner and only be visible and
/// used by those owners"): a word recorded to them (<see cref="VocabularyWordOwnership"/>),
/// whether or not a resource links it. A word another learner owns, or no one owns, stays on
/// the device and is never shown, even when it sits in the owner's resource.
/// </summary>
public sealed partial class NativeResourceStore
{
    public async Task<NativeVocabularyCatalog> ListOwnedVocabularyAsync(
        string owner, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return new(false, [], []);

        var stamp = userScope.CaptureStamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var progress = await scope.ServiceProvider
            .GetRequiredService<VocabularyProgressService>()
            .GetAllProgressDictionaryAsync(owner);
        // Every word recorded to the owner, linked to one of their resources or not, with the
        // owner's resources that link it.
        var words = (await scope.ServiceProvider
                .GetRequiredService<LearningResourceRepository>()
                .GetAllVocabularyWordsWithResourcesAsync(owner))
            .Where(word => VocabularyWordOwnership.IsLearners(word.UserProfileId, owner))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var ids = words.Select(word => word.Id).ToArray();
        // Only the owner's sentences count (recorded to them).
        var exampleCounts = ids.Length == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await OwnedExamples(db, owner)
                .Where(example => ids.Contains(example.VocabularyWordId))
                .GroupBy(example => example.VocabularyWordId)
                .Select(group => new { Id = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.Id, row => row.Count, cancellationToken);
        var resources = await db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner)
            .OrderBy(resource => resource.Title)
            .ThenBy(resource => resource.Id)
            .Select(resource => new NativeVocabularyResource(
                resource.Id, resource.Title ?? string.Empty,
                resource.Language, resource.IsSmartResource))
            .ToListAsync(cancellationToken);
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return new(false, [], []);

        var byResourceId = resources.ToDictionary(resource => resource.Id,
            StringComparer.Ordinal);
        var entries = words.Select(word =>
        {
            progress.TryGetValue(word.Id, out var ownProgress);
            var associated = (word.LearningResources ?? [])
                .Where(resource => resource.UserProfileId == owner
                    && byResourceId.ContainsKey(resource.Id))
                .Select(resource => byResourceId[resource.Id])
                .DistinctBy(resource => resource.Id)
                .ToArray();
            return VocabularyEntry(word, associated, ownProgress,
                exampleCounts.GetValueOrDefault(word.Id));
        }).OrderByDescending(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        return new(true, entries, resources);
    }

    public async Task<NativeVocabularyDetail?> GetOwnedVocabularyAsync(
        string owner, string wordId, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(wordId))
            return null;

        var stamp = userScope.CaptureStamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
            .Where(candidate => candidate.Id == wordId)
            .SingleOrDefaultAsync(cancellationToken);
        if (word is null || !IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return null;

        var available = await db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner)
            .OrderBy(resource => resource.Title)
            .ThenBy(resource => resource.Id)
            .Select(resource => new NativeVocabularyResource(
                resource.Id, resource.Title ?? string.Empty,
                resource.Language, resource.IsSmartResource))
            .ToListAsync(cancellationToken);
        var associated = await db.ResourceVocabularyMappings.AsNoTracking()
            .Where(mapping => mapping.VocabularyWordId == wordId)
            .Join(db.LearningResources.AsNoTracking()
                    .Where(resource => resource.UserProfileId == owner),
                mapping => mapping.ResourceId, resource => resource.Id,
                (_, resource) => resource.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
        var associatedIds = associated.ToHashSet(StringComparer.Ordinal);
        var ownProgress = await db.VocabularyProgresses.AsNoTracking()
            .SingleOrDefaultAsync(progress => progress.VocabularyWordId == wordId
                && progress.UserId == owner, cancellationToken);
        // Only the owner's sentences (recorded to them), the rule the delete applies; core
        // first, then oldest first (source ReferenceSentencesSection OrderedSentences).
        var examples = await OwnedExampleRows(db, owner, wordId)
            .ToListAsync(cancellationToken);
        var exampleCount = examples.Count;

        var hasConstituentLinks = await db.PhraseConstituents.AsNoTracking()
            .AnyAsync(link => link.PhraseWordId == wordId, cancellationToken);
        // A part another learner owns, or no one owns, is not shown.
        var constituents = await db.PhraseConstituents.AsNoTracking()
            .Where(link => link.PhraseWordId == wordId && link.ConstituentWordId != null)
            .Join(db.VocabularyWords.AsNoTracking().OwnedBy(owner),
                link => link.ConstituentWordId, candidate => candidate.Id,
                (_, candidate) => new NativeVocabularyConstituent(
                    candidate.Id, candidate.TargetLanguageTerm ?? string.Empty,
                    candidate.NativeLanguageTerm ?? string.Empty))
            .ToListAsync(cancellationToken);
        List<NativeVocabularyHistory> history = ownProgress is null
            ? new List<NativeVocabularyHistory>()
            : await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.VocabularyProgressId == ownProgress.Id)
                .OrderByDescending(context => context.LearnedAt)
                .Select(context => new NativeVocabularyHistory(
                    context.Id, context.Activity, context.LearnedAt,
                    context.WasCorrect,
                    context.InputMode == "Passive" || context.ContextType == "Exposure",
                    db.LearningResources.Where(resource =>
                            resource.Id == context.LearningResourceId
                            && resource.UserProfileId == owner)
                        .Select(resource => resource.Title).FirstOrDefault()))
                .ToListAsync(cancellationToken);
        var deleteBlock = await DeleteBlockAsync(db, owner, wordId, cancellationToken);
        if (!await OwnedVocabularyIds(db, owner).AnyAsync(id => id == wordId, cancellationToken)
            || !IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return null;

        var entry = VocabularyEntry(word,
            available.Where(resource => associatedIds.Contains(resource.Id)).ToArray(),
            ownProgress, exampleCount);
        var draft = new NativeVocabularyEditDraft(
            word.Id, word.UpdatedAt,
            word.TargetLanguageTerm ?? string.Empty,
            word.NativeLanguageTerm ?? string.Empty,
            word.Lemma, word.Language, word.Tags,
            word.MnemonicText, word.MnemonicImageUri,
            word.LexicalUnitType, associated);
        var progressSnapshot = ownProgress is null ? null
            : new NativeVocabularyProgress(ownProgress.Status, ownProgress.IsKnown,
                ownProgress.MasteryScore, ownProgress.CurrentStreak,
                ownProgress.ProductionInStreak, ownProgress.NextReviewDate,
                ownProgress.TotalAttempts);
        return new(entry, draft, available, progressSnapshot, history,
            examples, constituents, hasConstituentLinks, deleteBlock);
    }

    /// <summary>
    /// Whether the word is still recorded to the owner, without loading its detail: the
    /// editor's audio and image preview check only this.
    /// </summary>
    public async Task<bool> OwnsVocabularyAsync(
        string owner, string wordId, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(wordId))
            return false;
        var stamp = userScope.CaptureStamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await OwnedVocabularyIds(db, owner).AnyAsync(id => id == wordId, cancellationToken)
            && IsActiveOwner(owner) && userScope.IsCurrent(stamp);
    }

    /// <summary>
    /// Saves the owner's word and its links to their resources. Only a word recorded to the
    /// owner is written (its recorded owner may always edit it: nobody else can see it). A word
    /// with no links stays the owner's, so clearing every resource is allowed. Links to
    /// resources that are not the owner's are never read or changed.
    /// </summary>
    public async Task<NativeVocabularySaveStatus> SaveOwnedVocabularyAsync(
        string owner, NativeVocabularyEditDraft draft,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || draft is null
            || string.IsNullOrWhiteSpace(draft.Id))
            return NativeVocabularySaveStatus.NotFoundOrForbidden;
        if (string.IsNullOrWhiteSpace(draft.TargetTerm)
            || string.IsNullOrWhiteSpace(draft.NativeTerm)
            || !Enum.IsDefined(draft.LexicalUnitType)
            || draft.ResourceIds is null)
            return NativeVocabularySaveStatus.Invalid;

        var stamp = userScope.CaptureStamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken);
        var word = await db.VocabularyWords.OwnedBy(owner)
            .Where(candidate => candidate.Id == draft.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (word is null)
            return NativeVocabularySaveStatus.NotFoundOrForbidden;
        if (word.UpdatedAt != draft.OriginalUpdatedAt)
            return NativeVocabularySaveStatus.Conflict;
        if (draft.LexicalUnitType is LexicalUnitType.Unknown
                or LexicalUnitType.Idiom
            && word.LexicalUnitType != draft.LexicalUnitType)
            return NativeVocabularySaveStatus.Invalid;
        if (word.LexicalUnitType != draft.LexicalUnitType
            && await db.PhraseConstituents.AnyAsync(
                link => link.PhraseWordId == draft.Id, cancellationToken))
            return NativeVocabularySaveStatus.Invalid;

        var selectedIds = draft.ResourceIds
            .ToHashSet(StringComparer.Ordinal);
        var selectedResources = await db.LearningResources.AsNoTracking()
            .Where(resource => selectedIds.Contains(resource.Id)
                && resource.UserProfileId == owner)
            .Select(resource => new { resource.Id, resource.IsSmartResource })
            .ToListAsync(cancellationToken);
        if (selectedResources.Count != selectedIds.Count)
            return NativeVocabularySaveStatus.NotFoundOrForbidden;
        var currentMappings = await db.ResourceVocabularyMappings
            .Where(mapping => mapping.VocabularyWordId == draft.Id
                && db.LearningResources.Any(resource =>
                    resource.Id == mapping.ResourceId
                    && resource.UserProfileId == owner))
            .ToListAsync(cancellationToken);
        var currentIds = currentMappings.Select(mapping => mapping.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
        var currentSmartIds = await db.LearningResources.AsNoTracking()
            .Where(resource => currentIds.Contains(resource.Id)
                && resource.UserProfileId == owner && resource.IsSmartResource)
            .Select(resource => resource.Id)
            .ToListAsync(cancellationToken);
        if (selectedResources.Any(resource =>
                resource.IsSmartResource && !currentIds.Contains(resource.Id))
            || currentSmartIds.Any(resourceId => !selectedIds.Contains(resourceId)))
            return NativeVocabularySaveStatus.Invalid;

        var targetKey = VocabularyDuplicatePolicy.NormalizeTargetTerm(draft.TargetTerm);
        var nativeKey = VocabularyDuplicatePolicy.NormalizeKeyPart(draft.NativeTerm);
        var candidateWords = await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
            .Where(candidate => candidate.Id != draft.Id)
            .Select(candidate => new {
                candidate.TargetLanguageTerm, candidate.NativeLanguageTerm
            }).ToListAsync(cancellationToken);
        if (candidateWords.Any(candidate =>
            VocabularyDuplicatePolicy.NormalizeTargetTerm(candidate.TargetLanguageTerm)
                == targetKey
            && VocabularyDuplicatePolicy.NormalizeKeyPart(candidate.NativeLanguageTerm)
                == nativeKey))
            return NativeVocabularySaveStatus.Duplicate;

        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return NativeVocabularySaveStatus.NotFoundOrForbidden;
        word.TargetLanguageTerm = draft.TargetTerm.Trim();
        word.NativeLanguageTerm = draft.NativeTerm.Trim();
        word.Lemma = draft.Lemma?.Trim();
        word.Language = string.IsNullOrWhiteSpace(draft.Language)
            ? null : draft.Language.Trim();
        word.Tags = draft.Tags?.Trim();
        word.MnemonicText = draft.MnemonicText?.Trim();
        word.MnemonicImageUri = draft.MnemonicImageUri?.Trim();
        word.LexicalUnitType = draft.LexicalUnitType;
        word.UpdatedAt = DateTime.UtcNow;
        foreach (var mapping in currentMappings.Where(mapping =>
            !selectedIds.Contains(mapping.ResourceId)))
            db.ResourceVocabularyMappings.Remove(mapping);
        foreach (var resourceId in selectedIds.Except(currentIds))
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                ResourceId = resourceId, VocabularyWordId = word.Id
            });
        await db.SaveChangesAsync(cancellationToken);
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return NativeVocabularySaveStatus.NotFoundOrForbidden;
        await transaction.CommitAsync(cancellationToken);
        try
        {
            pendingWrites.MarkPending(owner);
            return NativeVocabularySaveStatus.Saved;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vocabulary save committed but sync tracking was not confirmed.");
            return NativeVocabularySaveStatus.SavedSyncUnconfirmed;
        }
    }

    /// <summary>The owner's own example sentences: recorded to them (the one ownership rule).</summary>
    private static IQueryable<ExampleSentence> OwnedExamples(
        ApplicationDbContext db, string owner) =>
        db.ExampleSentences.AsNoTracking().OwnedBy(owner);

    /// <summary>The owner's words: those recorded to them (the one ownership rule).</summary>
    private static IQueryable<string> OwnedVocabularyIds(
        ApplicationDbContext db, string owner) =>
        db.VocabularyWords.OwnedBy(owner).Select(word => word.Id);

    /// <summary>
    /// Why the owner may not delete their word, or None, by the rules the shared delete and the
    /// server apply (<see cref="VocabularyWordDependents"/>): data that is not theirs still uses
    /// it (<see cref="VocabularyWordDependents.OthersDependOnAsync"/>: another learner's, or no
    /// one's, link, progress, sentence, minimal pair or phrase), then Minimal Pairs answers,
    /// which restrict the delete in the database.
    /// </summary>
    private static async Task<NativeVocabularyDeleteBlock> DeleteBlockAsync(
        ApplicationDbContext db, string owner, string wordId,
        CancellationToken cancellationToken)
    {
        string[] ids = [wordId];
        if ((await VocabularyWordDependents.OthersDependOnAsync(
                db, ids, owner, cancellationToken)).Count > 0)
            return NativeVocabularyDeleteBlock.UsedByOthers;
        return (await VocabularyWordDependents.AnswersStillUseAsync(
                db, ids, cancellationToken)).Count > 0
            ? NativeVocabularyDeleteBlock.UsedByAnswers : NativeVocabularyDeleteBlock.None;
    }

    private static NativeVocabularyEntry VocabularyEntry(
        VocabularyWord word, IReadOnlyList<NativeVocabularyResource> resources,
        VocabularyProgress? progress, int exampleCount)
    {
        var (_, encodingLabel) = EncodingScoreHelper.CalculateWithLabel(word, exampleCount);
        return new NativeVocabularyEntry(
            word.Id, word.TargetLanguageTerm ?? string.Empty,
            word.NativeLanguageTerm ?? string.Empty, word.LexicalUnitType,
            word.Tags, word.Lemma, word.Language,
            word.CreatedAt, word.UpdatedAt, encodingLabel,
            progress?.Status ?? LearningStatus.Unknown,
            progress?.IsKnown == true, resources);
    }
}
