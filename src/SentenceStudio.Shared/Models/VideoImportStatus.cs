namespace SentenceStudio.Shared.Models;

/// <summary>
/// Tracks the pipeline stages for a YouTube video import.
/// </summary>
public enum VideoImportStatus
{
    /// <summary>Import queued, not yet started.</summary>
    Pending = 0,

    /// <summary>Fetching transcript from YouTube.</summary>
    FetchingTranscript = 1,

    /// <summary>Cleaning transcript with AI polish.</summary>
    CleaningTranscript = 2,

    /// <summary>Generating vocabulary from cleaned transcript.</summary>
    GeneratingVocabulary = 3,

    /// <summary>Saving LearningResource and VocabularyWords.</summary>
    SavingResource = 4,

    /// <summary>Pipeline completed successfully.</summary>
    Completed = 10,

    /// <summary>Pipeline failed — see ErrorMessage.</summary>
    Failed = 99
}
