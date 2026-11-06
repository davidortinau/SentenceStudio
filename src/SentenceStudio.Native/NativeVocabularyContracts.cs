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
    bool HasConstituentLinks,
    NativeVocabularyDeleteBlock DeleteBlock);

/// <summary>
/// Why the word cannot be deleted, by the rules the server applies to a synced word delete,
/// or <see cref="None"/>. The word itself is always the owner's: only a word recorded to them
/// is shown (Captain 2026-11-06), and its recorded owner may always edit it.
/// </summary>
public enum NativeVocabularyDeleteBlock
{
    None,
    /// <summary>
    /// Data that is not the owner's (another learner's, or recorded to no one) still uses the
    /// word: a resource link, progress, an example sentence, a minimal pair or a phrase. The
    /// delete would remove or change that row, so the server keeps the word.
    /// </summary>
    UsedByOthers,
    /// <summary>A Minimal Pairs answer references the word, which the database refuses to delete (B2).</summary>
    UsedByAnswers
}

public enum NativeVocabularySaveStatus
{
    Saved, SavedSyncUnconfirmed, NotFoundOrForbidden,
    Invalid, Duplicate, Conflict
}

/// <summary>
/// Source VocabularyWordEdit ConfirmDelete, refused for a word the server would keep
/// (<see cref="NativeVocabularyDeleteBlock"/>).
/// </summary>
public enum NativeVocabularyDeleteStatus
{
    Deleted, DeletedSyncUnconfirmed, NotFoundOrForbidden,
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
/// One copy in a duplicate group (source VocabularyDuplicateWordInfo): always one of the
/// owner's own words, shown with its memory aids.
/// </summary>
public sealed record NativeVocabularyDuplicateWord(
    string Id, string TargetTerm, string NativeTerm,
    string? Language, LexicalUnitType LexicalUnitType,
    int ResourceCount, double EncodingScore, string EncodingLabel,
    string? MnemonicText, bool HasImageAid, bool HasAudio);

/// <summary>
/// Source VocabularyDuplicateGroup of the owner's own words; <see cref="Words"/> is in
/// merge-preference order.
/// </summary>
public sealed record NativeVocabularyDuplicateGroup(
    string NormalizedTerm, string DisplayTerm,
    IReadOnlyList<NativeVocabularyDuplicateWord> Words,
    bool CanMergeAutomatically, string? MergeBlockedReason,
    string RecommendedKeeperId);

/// <summary>Source VocabularyDuplicateScanResult for the active owner.</summary>
public sealed record NativeVocabularyDuplicateScan(
    bool OwnerValid,
    IReadOnlyList<NativeVocabularyDuplicateGroup> Groups,
    int TotalGroupCount, int ExtraRecordCount);
