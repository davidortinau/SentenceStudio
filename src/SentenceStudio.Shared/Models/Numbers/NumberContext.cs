using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models.Numbers;

[Table("NumberContext")]
public class NumberContext
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public NumberSystem DefaultSystem { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
