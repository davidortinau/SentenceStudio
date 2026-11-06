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
    /// No such word in the learner's vocabulary, or no active learner (nothing was read or
    /// written).
    /// </summary>
    NotFound,

    /// <summary>
    /// The word is not recorded to the learner, or another learner's data (or data recorded to
    /// nobody) still uses it: the delete would remove or change their rows. Nothing changed.
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

/// <summary>How many words a bulk delete removed, kept (refused) and failed to delete.</summary>
public sealed record VocabularyBulkDeleteResult(int Deleted, int Kept, int Failed);

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
/// A word as the web/MAUI editor shows it to a learner. A word that is not
/// <see cref="IsLearnersOwn"/> is read-only and carries no memory aids or audio.
/// </summary>
public sealed record LearnerVocabularyWord(SentenceStudio.Shared.Models.VocabularyWord Word, bool IsLearnersOwn);
