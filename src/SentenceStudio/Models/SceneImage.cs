using SQLite;

namespace SentenceStudio.Models;

public class SceneImage
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string Url { get; set; }
    public string Description { get; set; }    
    
}



