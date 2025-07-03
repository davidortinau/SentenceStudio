using System.ComponentModel;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

[Table("Challenges")]
public partial class Challenge : ObservableObject
{
    [JsonIgnore]
    public int Id { get; set; }
    
    [ObservableProperty]
    private string? sentenceText;
    
    [ObservableProperty]
    private string? recommendedTranslation;
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; }
    
    [JsonIgnore]
    public DateTime UpdatedAt { get; set; }
    
    [Description("Includes all the English words to complete the full sentence, but only include the Korean if the word is necessary for a natural Korean sentence. Use the Korean dictionary form in the vocabulary array.")]
    [NotMapped] 
    public List<VocabularyWord>? Vocabulary { get; set; }
    
    [ObservableProperty]
    private string? vocabularyWord;
    
    [ObservableProperty]
    private string? vocabularyWordAsUsed;
    
    [Description("Five comma separated words from which the user can choose, including the correct vocabulary word.")]
    [ObservableProperty]
    private string? vocabularyWordGuesses;
    
    [JsonIgnore]
    [NotMapped]
    public UserActivity? UserActivity { get; set; }

    [JsonIgnore]
    [NotMapped]
    public bool IsCurrent { get; set; }
}
