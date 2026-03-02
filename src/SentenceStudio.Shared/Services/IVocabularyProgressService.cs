using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Interface for vocabulary progress service with enhanced tracking capabilities
/// </summary>
public interface IVocabularyProgressService
{
    /// <summary>
    /// Records a vocabulary learning attempt and updates progress
    /// </summary>
    Task<VocabularyProgress> RecordAttemptAsync(VocabularyAttempt attempt);

    /// <summary>
    /// Gets progress for a specific vocabulary word and user
    /// </summary>
    Task<VocabularyProgress> GetProgressAsync(int vocabularyWordId, int userId = 1);

    /// <summary>
    /// Gets words that are due for review based on spaced repetition schedule
    /// </summary>
    Task<List<VocabularyProgress>> GetReviewCandidatesAsync(int userId = 1);

    /// <summary>
    /// Gets all progress records for a user
    /// </summary>
    Task<List<VocabularyProgress>> GetAllProgressAsync(int userId = 1);

    /// <summary>
    /// Gets progress for multiple vocabulary words and returns as dictionary
    /// </summary>
    Task<Dictionary<int, VocabularyProgress>> GetProgressForWordsAsync(List<int> vocabularyWordIds, int userId = 1);

    /// <summary>
    /// Gets ALL progress records for a user and returns as dictionary keyed by VocabularyWordId
    /// OPTIMIZATION: Use this instead of GetProgressForWordsAsync when loading all vocabulary
    /// </summary>
    Task<Dictionary<int, VocabularyProgress>> GetAllProgressDictionaryAsync(int userId = 0);

    /// <summary>
    /// Legacy method: Gets or creates progress for a vocabulary word (backward compatibility)
    /// </summary>
    Task<VocabularyProgress> GetOrCreateProgressAsync(int vocabularyWordId);

    /// <summary>
    /// Legacy method: Records a correct answer (backward compatibility)
    /// </summary>
    Task<VocabularyProgress> RecordCorrectAnswerAsync(
        int vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        int? learningResourceId = null);

    /// <summary>
    /// Legacy method: Records an incorrect answer (backward compatibility)
    /// </summary>
    Task<VocabularyProgress> RecordIncorrectAnswerAsync(
        int vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        int? learningResourceId = null);

    /// <summary>
    /// Migrates existing vocabulary progress to streak-based scoring system.
    /// Returns the count of records migrated.
    /// </summary>
    Task<int> MigrateToStreakBasedScoringAsync();
}