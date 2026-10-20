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
    int Id, string TargetSentence, string? NativeSentence);

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
    bool HasConstituentLinks);

public enum NativeVocabularySaveStatus
{
    Saved, SavedSyncUnconfirmed, NotFoundOrForbidden, SharedReadOnly,
    Invalid, Duplicate, Conflict
}
