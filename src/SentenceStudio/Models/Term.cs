using SQLite;

namespace SentenceStudio.Models;

public class Term
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string TargetLanguageTerm { get; set; }
    public string NativeLanguageTerm { get; set; }
    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int VocabularyListId { get; set; } 
    
}



