using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("SceneImages")]
public partial class SceneImage : ObservableObject
{
    // Id property for CoreSync (Entity Framework/Database)
    public int Id { get; set; }
    
    // Original properties 
    public string? Url { get; set; }
    public string? Description { get; set; }
    
    // Observable property for UI binding
    [ObservableProperty]
    [NotMapped]
    private bool _isSelected;

    // Constructors
    public SceneImage() { }
    
    public SceneImage(string url, string description, bool isSelected = false)
    {
        Url = url;
        Description = description;
        IsSelected = isSelected;
    }
}
