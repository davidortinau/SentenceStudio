namespace SentenceStudio.Contracts.Vocabulary;

public sealed class VocabularyProgressResponse
{
    public string Id { get; set; } = string.Empty;
    public string VocabularyWordId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public float MasteryScore { get; set; }
    public bool IsUserDeclared { get; set; }
    public DateTime? UserDeclaredAt { get; set; }
    public string VerificationState { get; set; } = string.Empty;
    public float CurrentStreak { get; set; }
    public int TotalAttempts { get; set; }
}
