using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

internal sealed record VocabularyListPresentation(
    bool Loading, bool Blocked, string? ErrorKey,
    NativeVocabularyCatalog Catalog,
    IReadOnlyList<NativeVocabularyEntry> FilteredWords,
    VocabularyFilterState Filters,
    string SortField, bool SortDescending, bool ListMode,
    string? Feedback, bool FeedbackWarning,
    VocabularyDuplicatePanel? Duplicates = null);

internal sealed record VocabularyEditPresentation(
    bool Loading, bool Saving, NativeVocabularyDetail? Detail,
    NativeVocabularyEditDraft? Draft, string? ErrorKey,
    bool PlayingAudio, NativeQuizPhotoAsset? Photo,
    bool Deleting = false, string? DeleteError = null)
{
    /// <summary>Source VocabularyWordEdit: Find Duplicates is disabled while saving.</summary>
    internal bool CanFindDuplicates =>
        VocabularyWordCommands.CanFindDuplicates(Saving || Deleting, Draft);

    /// <summary>Source Delete, for a word only this owner uses.</summary>
    internal bool CanDelete => VocabularyWordCommands.CanDelete(Saving || Deleting, Detail);
}
