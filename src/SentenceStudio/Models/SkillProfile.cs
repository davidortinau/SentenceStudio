using SQLite;

namespace SentenceStudio.Models;

public class SkillProfile
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string Title { get; set; }
    public string Description {get; set;}
    public string Language {get;set;} = "Korean";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }    
    public override string ToString()
    {
        return Title;
    }
}