using SentenceStudio.Data;
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
    IReadOnlyList<NativeVocabularyResource> Resources)
{
    /// <summary>The owner's Learning mode: Mark Known is offered only in Learner-led.</summary>
    public LearningMode Mode { get; init; } = LearningMode.LearnerLed;
}

/// <summary>
/// What a learner's mark (Mark Known, Practice again, Start over) did: how many of the owner's
/// words changed, whether the save's sync tracking could not be recorded, and the resource key
/// of why nothing changed.
/// </summary>
public sealed record NativeVocabularyMarkResult(int Changed, bool SyncUnconfirmed = false,
    string? RefusalKey = null);

public sealed record NativeVocabularyProgress(
    LearningStatus Status, bool IsKnown, float MasteryScore,
    float CurrentStreak, int ProductionInStreak,
    DateTime? NextReviewDate, int TotalAttempts);

public sealed record NativeVocabularyHistory(
    string Id, string Activity, DateTime LearnedAt,
    bool WasCorrect, bool IsPassive, string? ResourceTitle);

/// <summary>
/// One of the owner's example sentences on the word (source ReferenceSentencesSection): its
/// text, whether it is the word's one core example, whether the learner flagged it, where it
/// came from, its speech register and difficulty (1 to 5, or none).
/// </summary>
public sealed record NativeVocabularyExample(
    string Id, string TargetSentence, string? NativeSentence,
    bool IsCore = false, bool IsFlagged = false,
    ExampleSentenceSource Source = ExampleSentenceSource.Unknown,
    SpeechRegister Register = SpeechRegister.Unspecified,
    int? DifficultyLevel = null);

/// <summary>A sentence the learner typed, edited or kept from the AI suggestions.</summary>
public sealed record NativeExampleSentenceDraft(
    string TargetSentence, string? NativeSentence,
    SpeechRegister Register = SpeechRegister.Unspecified,
    int? DifficultyLevel = null)
{
    /// <summary>ExampleSentence.TargetSentence and NativeSentence [MaxLength(500)], trimmed.</summary>
    public const int MaxLength = 500;

    /// <summary>Both texts, trimmed, fit their columns.</summary>
    public bool Fits => TargetSentence.Trim().Length <= MaxLength
        && (NativeSentence?.Trim().Length ?? 0) <= MaxLength;
}

public enum NativeExampleSentenceStatus
{
    Saved, SavedSyncUnconfirmed, NotFoundOrForbidden, Invalid
}

/// <summary>
/// What an example sentence write did, with the word's sentences after it (core first, then
/// oldest first) and the word's encoding label, which counts them.
/// </summary>
public sealed record NativeExampleSentenceResult(
    NativeExampleSentenceStatus Status,
    IReadOnlyList<NativeVocabularyExample> Examples,
    string EncodingLabel)
{
    public bool Saved => Status is NativeExampleSentenceStatus.Saved
        or NativeExampleSentenceStatus.SavedSyncUnconfirmed;
}

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
    NativeVocabularyDeleteBlock DeleteBlock)
{
    /// <summary>The owner's Learning mode (docs/known-words-plan.md).</summary>
    public LearningMode Mode { get; init; } = LearningMode.LearnerLed;

    /// <summary>Who decided a Known word is Known, for the status row's words.</summary>
    public SentenceStudio.Services.VocabularyKnownSource KnownSource { get; init; }

    /// <summary>Source word editor: a Known word offers Practice again.</summary>
    public bool CanPracticeAgain => Word.IsKnown;

    /// <summary>Source word editor: Mark Known for a word that is not Known, in Learner-led only.</summary>
    public bool CanMarkKnown => !Word.IsKnown && Mode == LearningMode.LearnerLed;

    /// <summary>Source word editor: Start over once the word has practice or a status.</summary>
    public bool CanStartOver => Progress is { TotalAttempts: > 0 } || Word.IsKnown
        || Word.Status != LearningStatus.Unknown;
}

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

/// <summary>A new word's save: its status and, once saved, the word's ID.</summary>
public sealed record NativeVocabularyCreateResult(NativeVocabularySaveStatus Status, string? WordId = null)
{
    public bool Saved => Status is NativeVocabularySaveStatus.Saved
        or NativeVocabularySaveStatus.SavedSyncUnconfirmed;
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
/// <param name="RefusalKey">The resource key of why nothing changed, or null.</param>
public sealed record NativeVocabularyMergeResult(int Removed, bool Changed, bool SyncUnconfirmed,
    string? RefusalKey = null)
{
    public static NativeVocabularyMergeResult None { get; } = new(0, false, false);
}

/// <summary>A group a merge refused: its display term and the resource key of why.</summary>
public sealed record NativeVocabularyMergeRefusal(string DisplayTerm, string RefusalKey);

/// <summary>Source MergeSafeDuplicateGroupsAsync for the active owner ("Merge all").</summary>
/// <param name="Refused">Safe groups the merge still refused (changed since the scan).</param>
public sealed record NativeVocabularyMergeAllResult(
    int MergedGroups, int RemovedCopies, IReadOnlyList<NativeVocabularyMergeRefusal> Refused,
    bool SyncUnconfirmed)
{
    public static NativeVocabularyMergeAllResult None { get; } = new(0, 0, [], false);
}

/// <summary>
/// "Resolve with AI" for the active owner (docs/duplicate-merge-ai-plan.md): what the rules merged,
/// what the AI applied (its decisions are listed, each with Undo), the calls left for the learner
/// and the refusals. <see cref="AiUnavailable"/>: the AI answered nothing, so it decided nothing.
/// </summary>
public sealed record NativeVocabularyDuplicateAiResult(
    int RulesRemovedCopies, int MergedGroups, int KeptApartGroups, int RemovedCopies,
    IReadOnlyList<NativeVocabularyDuplicateAiSuggestion> NeedsYou,
    IReadOnlyList<NativeVocabularyMergeRefusal> Refused,
    bool SyncUnconfirmed, bool AiUnavailable = false)
{
    public static NativeVocabularyDuplicateAiResult None { get; } = new(0, 0, 0, 0, [], [], false);
}

/// <summary>
/// The AI's call on a group it was not sure of, shown on the group for the learner to decide.
/// Not <see cref="Usable"/> when the AI gave no answer that passed the checks;
/// <see cref="Unanswered"/> when it did not answer for the group at all.
/// </summary>
public sealed record NativeVocabularyDuplicateAiSuggestion(
    string NormalizedTerm, bool Usable, SentenceStudio.Services.VocabularyDuplicateAiVerdict Verdict,
    IReadOnlyList<string> Meanings, string Reason, bool Unanswered = false);

/// <summary>
/// One of the owner's standing duplicate decisions (shared VocabularyDuplicateDecisionSummary),
/// listed on the duplicate page with its Undo.
/// </summary>
public sealed record NativeVocabularyDuplicateDecision(
    string Id, VocabularyDuplicateDecisionKind Kind, string DisplayTerm, IReadOnlyList<string> Meanings,
    VocabularyDuplicateDecider DecidedBy, string? Reason);

/// <summary>
/// One copy in a duplicate group (source VocabularyDuplicateWordInfo): always one of the
/// owner's own words, shown with its memory aids.
/// </summary>
public sealed record NativeVocabularyDuplicateWord(
    string Id, string TargetTerm, string NativeTerm,
    string? Language, LexicalUnitType LexicalUnitType,
    int ResourceCount, double EncodingScore, string EncodingLabel,
    string? MnemonicText, bool HasImageAid, bool HasAudio, string? Lemma = null,
    bool UsedByOthers = false, double Mastery = 0, int Attempts = 0);

/// <summary>
/// Source VocabularyDuplicateGroup of the owner's own words; <see cref="Words"/> is in
/// merge-preference order.
/// </summary>
/// <param name="MergeBlockedReason">The resource key of why it cannot merge, or null.</param>
/// <param name="Risk">Safe (merged by Merge all), review (the learner decides) or blocked.</param>
/// <param name="Differences">What merging would drop, when <paramref name="Risk"/> is review.</param>
public sealed record NativeVocabularyDuplicateGroup(
    string NormalizedTerm, string DisplayTerm,
    IReadOnlyList<NativeVocabularyDuplicateWord> Words,
    bool CanMergeAutomatically, string? MergeBlockedReason,
    string RecommendedKeeperId,
    VocabularyDuplicateRisk Risk = VocabularyDuplicateRisk.Safe,
    IReadOnlyList<VocabularyDuplicateDifference>? Differences = null,
    IReadOnlyList<VocabularyDuplicateMergePart>? MergeParts = null)
{
    public IReadOnlyList<VocabularyDuplicateDifference> DifferenceList => Differences ?? [];

    /// <summary>What Merge all merges in this group (the shared scan's parts; none for a test double).</summary>
    public IReadOnlyList<VocabularyDuplicateMergePart> MergePartList => MergeParts ?? [];
}

/// <summary>Source VocabularyDuplicateScanResult for the active owner.</summary>
public sealed record NativeVocabularyDuplicateScan(
    bool OwnerValid,
    IReadOnlyList<NativeVocabularyDuplicateGroup> Groups,
    int TotalGroupCount, int ExtraRecordCount,
    int SafeGroupCount = 0, int SafeCopyCount = 0, int ReviewGroupCount = 0, int BlockedGroupCount = 0);
// SafeGroupCount counts merges, not groups: a safe group is one, and each set of identical copies
// inside a group to review is one more (VocabularyDuplicateScanResult).
