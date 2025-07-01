namespace SentenceStudio.Shared.Models;

public partial class SceneImage
{
    public int ID { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public bool IsSelected { get; set; }
}
