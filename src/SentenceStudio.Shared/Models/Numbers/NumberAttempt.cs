using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models.Numbers;

[Table("NumberAttempt")]
public class NumberAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserProfileId { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public string ContextCode { get; set; } = string.Empty;
    public string SubModeCode { get; set; } = string.Empty;
    public string? CounterId { get; set; }
    public NumberSystem System { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public string PromptValue { get; set; } = string.Empty;
    public string ExpectedAnswer { get; set; } = string.Empty;
    public string? UserAnswer { get; set; }
    public bool IsCorrect { get; set; }
    public string? ErrorClass { get; set; }
    public int LatencyMs { get; set; }
    public DateTime AttemptedAt { get; set; } = DateTime.Now;
}
