
using System.Text.Json.Serialization;

namespace SentenceStudio.Models;

public class Story
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }

    public int ListID {get;set;}
    public int SkillID {get;set;}

    [JsonPropertyName("body")]
    public string Body { get; set; }

    [Ignore]
    [JsonPropertyName("questions")]
    public List<Question> Questions { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}