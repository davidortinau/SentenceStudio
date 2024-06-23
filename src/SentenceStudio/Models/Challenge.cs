
using System.Text.Json.Serialization;
using SQLite;

namespace SentenceStudio.Models;
public partial class Challenge : ObservableObject
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    
    [JsonPropertyName("sentence")]
    public string SentenceText { get; set; }

    [JsonPropertyName("recommended_translation")]
    public string RecommendedTranslation { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [Ignore]
    [JsonPropertyName("vocabulary")]
    public List<VocabularyWord> Vocabulary { get; set; }

    [JsonPropertyName("vocabulary_word")]
    public string VocabularyWord {get;set;}

    [JsonPropertyName("vocabulary_word_used")]
    public string VocabularyWordAsUsed { get; set; } 

    [JsonPropertyName("vocabulary_word_used_guesses")]
    public string VocabularyWordGuesses { get; set; }

    [ObservableProperty]
    private UserActivity _userActivity;
}