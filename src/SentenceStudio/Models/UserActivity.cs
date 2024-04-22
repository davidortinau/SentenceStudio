using SQLite;

namespace SentenceStudio.Models;

public class UserActivity
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string Activity { get; set; }
    public string Input {get; set;}
    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
}



