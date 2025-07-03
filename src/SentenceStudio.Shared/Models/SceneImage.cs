using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

public partial class SceneImage : ObservableObject
{
    // ID property for CoreSync (Entity Framework/Database)
    public int ID { get; set; }
    
    // Original properties 
    public string? Url { get; set; }
    public string? Description { get; set; }
    
    // Observable property for UI binding
    [ObservableProperty]
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
