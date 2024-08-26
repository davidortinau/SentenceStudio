
using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;

namespace SentenceStudio.Models;
public partial class Question
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    
    [JsonPropertyName("question")]
    public string Body { get; set; }

    [JsonPropertyName("answer")]
    public string Answer { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int StoryID { get; set; }     
}