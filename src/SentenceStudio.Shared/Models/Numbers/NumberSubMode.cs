using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models.Numbers;

[Table("NumberSubMode")]
public class NumberSubMode
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Phase { get; set; }
    public bool IsActive { get; set; } = true;
}
