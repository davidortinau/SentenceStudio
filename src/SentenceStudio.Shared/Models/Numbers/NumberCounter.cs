using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models.Numbers;

[Table("NumberCounter")]
public class NumberCounter
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string LanguageCode { get; set; } = string.Empty;
    public string Counter { get; set; } = string.Empty;
    public string Romanization { get; set; } = string.Empty;
    public string MeaningEn { get; set; } = string.Empty;
    public NumberSystem System { get; set; }
    public string? Notes { get; set; }
}
