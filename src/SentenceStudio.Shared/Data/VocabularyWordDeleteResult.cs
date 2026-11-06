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
