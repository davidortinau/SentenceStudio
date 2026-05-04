using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models.Numbers;

[Table("NumberMasteryProgress")]
public class NumberMasteryProgress
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserProfileId { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public string ContextCode { get; set; } = string.Empty;
    public string? CounterId { get; set; }
    public NumberSystem System { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public int CorrectCount { get; set; }
    public int TotalCount { get; set; }
    public int MedianLatencyMs { get; set; }
    public double EaseFactor { get; set; } = 2.5;
    public int Interval { get; set; }
    public int Repetitions { get; set; }
    public DateTime DueDate { get; set; } = DateTime.Now;
    public DateTime? LastReviewed { get; set; }
}
