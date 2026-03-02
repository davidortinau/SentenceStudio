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
    Task<VocabularyProgress> GetProgressAsync(string vocabularyWordId, string userId = "");

    /// <summary>
    /// Gets words that are due for review based on spaced repetition schedule
    /// </summary>
    Task<List<VocabularyProgress>> GetReviewCandidatesAsync(string userId = "");

    /// <summary>
    /// Gets all progress records for a user
    /// </summary>
    Task<List<VocabularyProgress>> GetAllProgressAsync(string userId = "");

    /// <summary>
    /// Gets progress for multiple vocabulary words and returns as dictionary
    /// </summary>
    Task<Dictionary<string, VocabularyProgress>> GetProgressForWordsAsync(List<string> vocabularyWordIds, string userId = "");

    /// <summary>
    /// Gets ALL progress records for a user and returns as dictionary keyed by VocabularyWordId
    /// OPTIMIZATION: Use this instead of GetProgressForWordsAsync when loading all vocabulary
    /// </summary>
    Task<Dictionary<string, VocabularyProgress>> GetAllProgressDictionaryAsync(string userId = "");

    /// <summary>
    /// Legacy method: Gets or creates progress for a vocabulary word (backward compatibility)
    /// </summary>
    Task<VocabularyProgress> GetOrCreateProgressAsync(string vocabularyWordId);

    /// <summary>
    /// Legacy method: Records a correct answer (backward compatibility)
    /// </summary>
    Task<VocabularyProgress> RecordCorrectAnswerAsync(
        string vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        string? learningResourceId = null);

    /// <summary>
    /// Legacy method: Records an incorrect answer (backward compatibility)
    /// </summary>
    Task<VocabularyProgress> RecordIncorrectAnswerAsync(
        string vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        string? learningResourceId = null);

    /// <summary>
    /// Migrates existing vocabulary progress to streak-based scoring system.
    /// Returns the count of records migrated.
    /// </summary>
    Task<int> MigrateToStreakBasedScoringAsync();
}