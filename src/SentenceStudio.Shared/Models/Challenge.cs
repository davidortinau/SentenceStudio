using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

public partial class Challenge : ObservableObject
{
    [JsonIgnore]
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    
    [ObservableProperty]
    private string? sentenceText;
    
    [ObservableProperty]
    private string? recommendedTranslation;
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; }
    
    [JsonIgnore]
    public DateTime UpdatedAt { get; set; }
    
    [Description("Includes all the English words to complete the full sentence, but only include the Korean if the word is necessary for a natural Korean sentence. Use the Korean dictionary form in the vocabulary array.")]
    [Ignore] 
    public List<VocabularyWord>? Vocabulary { get; set; }
    
    [ObservableProperty]
    private string? vocabularyWord;
    
    [ObservableProperty]
    private string? vocabularyWordAsUsed;
    
    [Description("Five comma separated words from which the user can choose, including the correct vocabulary word.")]
    [ObservableProperty]
    private string? vocabularyWordGuesses;
    
    [JsonIgnore]
    [Ignore]
    public UserActivity? UserActivity { get; set; }

    [JsonIgnore]
    [Ignore]
    public bool IsCurrent { get; set; }
}
