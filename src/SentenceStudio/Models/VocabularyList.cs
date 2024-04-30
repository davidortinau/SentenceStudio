using SQLite;

namespace SentenceStudio.Models;
public class VocabularyList
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    [Ignore]
    public List<Term>? Terms { get; set; }
}