namespace SentenceStudio.Contracts.Plans;

public sealed class StreakDto
{
    public required int CurrentStreak { get; init; }
    public required int LongestStreak { get; init; }

    /// <summary>User-local date of last recorded practice.</summary>
    public DateOnly? LastPracticeDate { get; init; }
}
