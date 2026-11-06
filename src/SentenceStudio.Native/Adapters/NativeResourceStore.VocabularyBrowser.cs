using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

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
        var mapped = await scope.ServiceProvider
            .GetRequiredService<LearningResourceRepository>()
            .GetAllVocabularyWordsWithResourcesAsync(owner);
        cancellationToken.ThrowIfCancellationRequested();

        var mappedIds = mapped.Select(word => word.Id)
            .ToHashSet(StringComparer.Ordinal);
        var progressOnlyIds = progress.Keys.Where(id => !mappedIds.Contains(id)).ToArray();
        List<VocabularyWord> progressOnly = progressOnlyIds.Length == 0
            ? new List<VocabularyWord>()
            : await db.VocabularyWords.AsNoTracking()
                .Where(word => progressOnlyIds.Contains(word.Id))
                .ToListAsync(cancellationToken);
        var words = mapped.Concat(progressOnly).ToArray();
        var ids = words.Select(word => word.Id).ToArray();
        // The one ownership rule (VocabularyWordOwnership): memory aids count only on the
        // learner's own words, and only the learner's sentences count.
        var learnersWords = await VocabularyWordOwnership.OwnedByAsync(
            db, ids, owner, cancellationToken);
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
            var associated = word.LearningResources
                .Where(resource => resource.UserProfileId == owner
                    && byResourceId.ContainsKey(resource.Id))
                .Select(resource => byResourceId[resource.Id])
                .DistinctBy(resource => resource.Id)
                .ToArray();
            return VocabularyEntry(word, associated, ownProgress,
                exampleCounts.GetValueOrDefault(word.Id),
                learnersWords.Contains(word.Id));
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
        var word = await db.VocabularyWords.AsNoTracking()
            .Where(candidate => candidate.Id == wordId
                && OwnedVocabularyIds(db, owner).Contains(candidate.Id))
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
        // Only the learner's sentences (recorded to them), the rule the delete applies.
        var examples = await OwnedExamples(db, owner)
            .Where(example => example.VocabularyWordId == wordId)
            .OrderBy(example => example.CreatedAt)
            .ThenBy(example => example.Id)
            .Select(example => new NativeVocabularyExample(
                example.Id, example.TargetSentence, example.NativeSentence))
            .ToListAsync(cancellationToken);
        var exampleCount = examples.Count;

        var hasConstituentLinks = await db.PhraseConstituents.AsNoTracking()
            .AnyAsync(link => link.PhraseWordId == wordId, cancellationToken);
        var constituents = await db.PhraseConstituents.AsNoTracking()
            .Where(link => link.PhraseWordId == wordId && link.ConstituentWordId != null)
            .Join(db.VocabularyWords.AsNoTracking()
                    .Where(candidate => OwnedVocabularyIds(db, owner).Contains(candidate.Id)),
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
        var ownsWord = (await VocabularyWordOwnership.OwnedByAsync(
            db, [wordId], owner, cancellationToken)).Contains(wordId);
        var editable = ownsWord
            && !(await VocabularyWordDependents.OthersUseAsync(
                db, [wordId], owner, cancellationToken)).Contains(wordId);
        var deleteBlock = await DeleteBlockAsync(db, owner, wordId, editable, cancellationToken);
        if (!await db.VocabularyWords.AsNoTracking()
                .AnyAsync(candidate => candidate.Id == wordId
                    && OwnedVocabularyIds(db, owner).Contains(candidate.Id),
                    cancellationToken)
            || !IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return null;

        var entry = VocabularyEntry(word,
            available.Where(resource => associatedIds.Contains(resource.Id)).ToArray(),
            ownProgress, exampleCount, ownsWord);
        var draft = new NativeVocabularyEditDraft(
            word.Id, word.UpdatedAt,
            word.TargetLanguageTerm ?? string.Empty,
            word.NativeLanguageTerm ?? string.Empty,
            word.Lemma, word.Language, word.Tags,
            ownsWord ? word.MnemonicText : null,
            ownsWord ? word.MnemonicImageUri : null,
            word.LexicalUnitType, associated);
        var progressSnapshot = ownProgress is null ? null
            : new NativeVocabularyProgress(ownProgress.Status, ownProgress.IsKnown,
                ownProgress.MasteryScore, ownProgress.CurrentStreak,
                ownProgress.ProductionInStreak, ownProgress.NextReviewDate,
                ownProgress.TotalAttempts);
        return new(entry, draft, available, progressSnapshot, history,
            examples, constituents, !editable, hasConstituentLinks, deleteBlock);
    }

    /// <summary>
    /// Whether the word is still one of the owner's (linked to their resource or practised),
    /// without loading its detail: the editor's audio and image preview check only this.
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
        var word = await db.VocabularyWords
            .Where(candidate => candidate.Id == draft.Id
                && OwnedVocabularyIds(db, owner).Contains(candidate.Id))
            .SingleOrDefaultAsync(cancellationToken);
        if (word is null)
            return NativeVocabularySaveStatus.NotFoundOrForbidden;
        if (!await IsEditableByOwnerAsync(db, owner, draft.Id, cancellationToken))
            return NativeVocabularySaveStatus.SharedReadOnly;
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
        if (selectedIds.Count == 0
            && !await db.VocabularyProgresses.AnyAsync(progress =>
                progress.VocabularyWordId == draft.Id && progress.UserId == owner,
                cancellationToken))
            return NativeVocabularySaveStatus.Invalid;

        var targetKey = VocabularyDuplicatePolicy.NormalizeTargetTerm(draft.TargetTerm);
        var nativeKey = VocabularyDuplicatePolicy.NormalizeKeyPart(draft.NativeTerm);
        var candidateWords = await db.VocabularyWords.AsNoTracking()
            .Where(candidate => candidate.Id != draft.Id
                && OwnedVocabularyIds(db, owner).Contains(candidate.Id))
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

    /// <summary>
    /// The learner's own example sentences: recorded to them (the one ownership rule). A
    /// sentence recorded to no one is no one's and counts as someone else's use
    /// (<see cref="VocabularyWordDependents.OthersUseAsync"/>).
    /// </summary>
    private static IQueryable<ExampleSentence> OwnedExamples(
        ApplicationDbContext db, string owner) =>
        db.ExampleSentences.AsNoTracking()
            .Where(example => example.UserProfileId == owner);

    private static IQueryable<string> OwnedVocabularyIds(
        ApplicationDbContext db, string owner) =>
        db.ResourceVocabularyMappings
            .Join(db.LearningResources.Where(resource =>
                    resource.UserProfileId == owner),
                mapping => mapping.ResourceId, resource => resource.Id,
                (mapping, _) => mapping.VocabularyWordId)
            .Union(db.VocabularyProgresses.Where(progress => progress.UserId == owner)
                .Select(progress => progress.VocabularyWordId));

    /// <summary>
    /// Whether the owner may change the word and its links (Save), by the shared rules: the
    /// word is recorded to them (<see cref="VocabularyWordOwnership.OwnedByAsync"/>, the one rule every
    /// client and the server use; a word recorded to no one is read-only for everyone) and no
    /// one else links, practises or wrote a sentence for it
    /// (<see cref="VocabularyWordDependents.OthersUseAsync"/>, the 2026-10-20 rule, a09596c3).
    /// Others' minimal pairs and phrases do not stop an edit: the edit changes neither row. A
    /// word stays editable after Save removes all its links while the owner keeps progress on
    /// it, as Save allows (review 4). The same rule decides whose memory aids the editor shows.
    /// </summary>
    private static async Task<bool> IsEditableByOwnerAsync(
        ApplicationDbContext db, string owner, string wordId,
        CancellationToken cancellationToken) =>
        (await VocabularyWordOwnership.OwnedByAsync(db, [wordId], owner, cancellationToken))
            .Contains(wordId)
        && !(await VocabularyWordDependents.OthersUseAsync(
            db, [wordId], owner, cancellationToken)).Contains(wordId);

    /// <summary>
    /// Why the owner may not delete the word, or None, by the rules the shared delete and the
    /// server apply (<see cref="VocabularyWordDependents"/>). A word they may not edit cannot go
    /// either; for an editable word the rest of
    /// <see cref="VocabularyWordDependents.OthersDependOnAsync"/> is others' minimal pairs and
    /// phrases (<see cref="VocabularyWordDependents.OthersPairsOrPhrasesUseAsync"/>), then
    /// Minimal Pairs answers, which restrict the delete in the database.
    /// </summary>
    private static async Task<NativeVocabularyDeleteBlock> DeleteBlockAsync(
        ApplicationDbContext db, string owner, string wordId, bool editable,
        CancellationToken cancellationToken)
    {
        if (!editable)
            return NativeVocabularyDeleteBlock.SharedReadOnly;
        string[] ids = [wordId];
        if ((await VocabularyWordDependents.OthersPairsOrPhrasesUseAsync(
                db, ids, owner, cancellationToken)).Count > 0)
            return NativeVocabularyDeleteBlock.UsedByOthers;
        return (await VocabularyWordDependents.AnswersStillUseAsync(
                db, ids, cancellationToken)).Count > 0
            ? NativeVocabularyDeleteBlock.UsedByAnswers : NativeVocabularyDeleteBlock.None;
    }

    private static NativeVocabularyEntry VocabularyEntry(
        VocabularyWord word, IReadOnlyList<NativeVocabularyResource> resources,
        VocabularyProgress? progress, int exampleCount, bool ownsMemoryAids)
    {
        var scoringWord = ownsMemoryAids ? word : new VocabularyWord {
            TargetLanguageTerm = word.TargetLanguageTerm,
            NativeLanguageTerm = word.NativeLanguageTerm
        };
        var (_, encodingLabel) = EncodingScoreHelper.CalculateWithLabel(
            scoringWord, exampleCount);
        return new NativeVocabularyEntry(
            word.Id, word.TargetLanguageTerm ?? string.Empty,
            word.NativeLanguageTerm ?? string.Empty, word.LexicalUnitType,
            word.Tags, word.Lemma, word.Language,
            word.CreatedAt, word.UpdatedAt, encodingLabel,
            progress?.Status ?? LearningStatus.Unknown,
            progress?.IsKnown == true, resources);
    }
}
