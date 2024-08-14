using SQLite;

namespace SentenceStudio.Models;

public partial class SceneImage : ObservableObject
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string Url { get; set; }
    public string Description { get; set; } 

    [ObservableProperty]
    private bool _isSelected;
    
}



