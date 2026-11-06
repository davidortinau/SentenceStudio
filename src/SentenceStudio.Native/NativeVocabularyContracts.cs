using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public sealed record NativeVocabularyResource(
    string Id, string Title, string? Language, bool IsSmartResource);

public sealed record NativeVocabularyEntry(
    string Id, string TargetTerm, string NativeTerm,
    LexicalUnitType LexicalUnitType, string? Tags, string? Lemma,
    string? Language, DateTime CreatedAt, DateTime UpdatedAt,
    string EncodingLabel, LearningStatus Status, bool IsKnown,
    IReadOnlyList<NativeVocabularyResource> AssociatedResources);

public sealed record NativeVocabularyCatalog(
    bool OwnerValid,
    IReadOnlyList<NativeVocabularyEntry> Words,
    IReadOnlyList<NativeVocabularyResource> Resources);

public sealed record NativeVocabularyProgress(
    LearningStatus Status, bool IsKnown, float MasteryScore,
    float CurrentStreak, int ProductionInStreak,
    DateTime? NextReviewDate, int TotalAttempts);

public sealed record NativeVocabularyHistory(
    string Id, string Activity, DateTime LearnedAt,
    bool WasCorrect, bool IsPassive, string? ResourceTitle);

public sealed record NativeVocabularyExample(
    string Id, string TargetSentence, string? NativeSentence);

public sealed record NativeVocabularyConstituent(
    string Id, string TargetTerm, string NativeTerm);

public sealed record NativeVocabularyEditDraft(
    string Id, DateTime OriginalUpdatedAt,
    string TargetTerm, string NativeTerm,
    string? Lemma, string? Language, string? Tags,
    string? MnemonicText, string? MnemonicImageUri,
    LexicalUnitType LexicalUnitType,
    IReadOnlyList<string> ResourceIds);

public sealed record NativeVocabularyDetail(
    NativeVocabularyEntry Word,
    NativeVocabularyEditDraft Draft,
    IReadOnlyList<NativeVocabularyResource> AvailableResources,
    NativeVocabularyProgress? Progress,
    IReadOnlyList<NativeVocabularyHistory> History,
    IReadOnlyList<NativeVocabularyExample> Examples,
    IReadOnlyList<NativeVocabularyConstituent> Constituents,
    bool IsSharedWithAnotherOwner,
    bool HasConstituentLinks,
    NativeVocabularyDeleteBlock DeleteBlock);

/// <summary>
/// Why the word cannot be deleted, by the rules the server applies to a synced word delete,
/// or <see cref="None"/>.
/// </summary>
public enum NativeVocabularyDeleteBlock
{
    None,
    /// <summary>The word cannot be edited either (<see cref="NativeVocabularyDetail.IsSharedWithAnotherOwner"/>).</summary>
    SharedReadOnly,
    /// <summary>
    /// Another learner's minimal pair or phrase, or one not recorded to anyone, uses the word:
    /// the delete would remove or change that row, so the server keeps the word.
    /// </summary>
    UsedByOthers,
    /// <summary>A Minimal Pairs answer references the word, which the database refuses to delete (B2).</summary>
    UsedByAnswers
}

public enum NativeVocabularySaveStatus
{
    Saved, SavedSyncUnconfirmed, NotFoundOrForbidden, SharedReadOnly,
    Invalid, Duplicate, Conflict
}

/// <summary>
/// Source VocabularyWordEdit ConfirmDelete, refused for a word the server would keep
/// (<see cref="NativeVocabularyDeleteBlock"/>).
/// </summary>
public enum NativeVocabularyDeleteStatus
{
    Deleted, DeletedSyncUnconfirmed, NotFoundOrForbidden, SharedReadOnly,
    UsedByOthers, UsedByAnswers, Failed
}

/// <summary>
/// What a duplicate merge did: copies removed, whether anything was saved (links and history
/// can move to the keeper while a copy other data references is kept), and whether the change
/// was saved but its sync tracking could not be recorded.
/// </summary>
public sealed record NativeVocabularyMergeResult(int Removed, bool Changed, bool SyncUnconfirmed)
{
    public static NativeVocabularyMergeResult None { get; } = new(0, false, false);
}

/// <summary>
/// One copy in a duplicate group (source VocabularyDuplicateWordInfo). Memory aids are
/// those of the copy's recorded owner only; another owner's copy shows none.
/// </summary>
public sealed record NativeVocabularyDuplicateWord(
    string Id, string TargetTerm, string NativeTerm,
    string? Language, LexicalUnitType LexicalUnitType,
    int ResourceCount, double EncodingScore, string EncodingLabel,
    string? MnemonicText, bool HasImageAid, bool HasAudio);

/// <summary>Source VocabularyDuplicateGroup; <see cref="Words"/> is in merge-preference order.</summary>
/// <param name="HasAnotherOwnersCopy">
/// A copy is not recorded to the learner (another learner's, or a legacy word recorded to
/// nobody). The group cannot merge on the device; each copy can still be opened.
/// </param>
public sealed record NativeVocabularyDuplicateGroup(
    string NormalizedTerm, string DisplayTerm,
    IReadOnlyList<NativeVocabularyDuplicateWord> Words,
    bool CanMergeAutomatically, string? MergeBlockedReason,
    string RecommendedKeeperId, bool HasAnotherOwnersCopy = false);

/// <summary>Source VocabularyDuplicateScanResult for the active owner.</summary>
public sealed record NativeVocabularyDuplicateScan(
    bool OwnerValid,
    IReadOnlyList<NativeVocabularyDuplicateGroup> Groups,
    int TotalGroupCount, int ExtraRecordCount);
