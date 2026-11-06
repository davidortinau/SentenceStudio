namespace SentenceStudio.Data;

/// <summary>
/// What <see cref="LearningResourceRepository.DeleteVocabularyWordAsync"/> did with a word
/// delete (B66). Every client says which one happened instead of always reporting success.
/// </summary>
public enum VocabularyWordDeleteResult
{
    /// <summary>The word, its links and everything that cascades from it are gone.</summary>
    Deleted,

    /// <summary>
    /// No such word recorded to the learner (another learner's or no one's word is not theirs to
    /// see), or no active learner (nothing was read or written).
    /// </summary>
    NotFound,

    /// <summary>
    /// The word is the learner's, but another learner's data (or data recorded to nobody) still
    /// uses it: the delete would remove or change their rows. Nothing changed.
    /// </summary>
    RefusedShared,

    /// <summary>
    /// A Minimal Pairs answer still references the word, which the database refuses to delete
    /// (B2). Nothing changed.
    /// </summary>
    RefusedAnswers,

    /// <summary>The delete failed; the error is logged and nothing was saved.</summary>
    Failed
}

/// <summary>
/// What a bulk delete did with each word: removed; kept because someone else's data still uses
/// it (<see cref="VocabularyWordDeleteResult.RefusedShared"/>); kept
/// because a Minimal Pairs answer references it; not found; failed. A kept word keeps the
/// learner's links.
/// </summary>
public sealed record VocabularyBulkDeleteResult(
    int Deleted, int KeptShared, int KeptAnswers, int NotFound, int Failed);

/// <summary>What the web/MAUI word editor's owner-scoped save did.</summary>
public enum VocabularyWordSaveResult
{
    Saved,

    /// <summary>The word is not the learner's (or there is no learner); nothing was written.</summary>
    NotLearnersOwn,

    /// <summary>The save failed; the error is logged.</summary>
    Failed
}

/// <summary>
/// What <see cref="LearningResourceRepository.UpdateVocabularyWordTermsAsync"/> did with a term
/// change: an expected refusal (the word is not the learner's, or is gone) is kept apart from a
/// technical failure, so a page never reports a database error as an ownership refusal.
/// </summary>
public enum VocabularyWordTermsUpdateResult
{
    Updated,

    /// <summary>No such word recorded to the learner (or no learner); nothing was written.</summary>
    NotLearnersOwn,

    /// <summary>The update failed; the error is logged and nothing was saved.</summary>
    Failed
}
