using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

public partial class Question : ObservableObject
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    
    [JsonPropertyName("question")]
    [ObservableProperty]
    private string? body;

    [JsonPropertyName("answer")]
    [ObservableProperty]
    private string? answer;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int StoryID { get; set; }     
}
