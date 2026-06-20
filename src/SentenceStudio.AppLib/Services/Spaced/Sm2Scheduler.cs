namespace SentenceStudio.Services.Spaced;

/// <summary>
/// Pure function implementation of the SM-2 (SuperMemo 2) spaced repetition algorithm.
/// Extracted from VocabularyProgressService for reuse across activities (vocabulary, numbers, etc.).
/// </summary>
public static class Sm2Scheduler
{
    private const int MaxReviewIntervalDays = 365; // Cap at 1 year maximum
    
    /// <summary>
    /// Result of an SM-2 schedule update calculation.
    /// </summary>
    /// <param name="EaseFactor">Updated ease factor (1.3-2.5 range)</param>
    /// <param name="Interval">Updated review interval in days</param>
    /// <param name="Repetitions">Updated repetition count (unused in current implementation but part of SM-2 spec)</param>
    /// <param name="DueDate">Next review date</param>
    public record Sm2Result(
        double EaseFactor,
        int Interval,
        int Repetitions,
        DateTime DueDate
    );
    
    /// <summary>
    /// Updates SM-2 schedule based on answer quality (0-5).
    /// Note: Current implementation uses simplified quality mapping (correct/incorrect) 
    /// rather than the full 0-5 quality scale. Quality parameter is preserved for 
    /// future enhancement (e.g., latency-based quality mapping in NumberDrill).
    /// </summary>
    /// <param name="easeFactor">Current ease factor (typically starts at 2.5)</param>
    /// <param name="interval">Current interval in days</param>
    /// <param name="repetitions">Current repetition count (not used in calculation but part of SM-2 state)</param>
    /// <param name="quality">Quality of recall: 0-2 = incorrect, 3-5 = correct (higher = better recall)</param>
    /// <returns>Updated SM-2 parameters and next due date</returns>
    public static Sm2Result Update(double easeFactor, int interval, int repetitions, int quality)
    {
        // SM-2 quality scale: 0-2 = failure, 3-5 = success
        bool wasCorrect = quality >= 3;
        
        double newEaseFactor;
        int newInterval;
        int newRepetitions = repetitions;
        
        if (!wasCorrect)
        {
            // Soften the lapse: proportional stability reduction instead of a hard reset
            // to 1. A single slip on a long-interval item should not send it back to
            // "due tomorrow". Repeated failures still compound down toward 1. Keep ~20%.
            newInterval = Math.Max(1, (int)Math.Round(interval * 0.2));
            newEaseFactor = Math.Max(1.3, easeFactor - 0.2);
        }
        else
        {
            // Increase interval on correct answer
            if (interval == 1)
            {
                newInterval = 6;
                newEaseFactor = Math.Min(2.5, easeFactor + 0.1);
            }
            else
            {
                newInterval = (int)(interval * easeFactor);
                // Cap the interval to prevent DateTime.AddDays overflow
                newInterval = Math.Min(newInterval, MaxReviewIntervalDays);
                newEaseFactor = Math.Min(2.5, easeFactor + 0.1);
            }
        }
        
        DateTime dueDate = DateTime.UtcNow.AddDays(newInterval);
        
        return new Sm2Result(newEaseFactor, newInterval, newRepetitions, dueDate);
    }
}
