using System.ComponentModel;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyWords")]
public partial class VocabularyWord : ObservableObject
{
    public int Id { get; set; }

    [Description("The word in the user's native language, usually English.")]
    [ObservableProperty]
    private string? nativeLanguageTerm;

    [Description("The word in the language being learned, usually Korean.")]
    [ObservableProperty]
    private string? targetLanguageTerm;

    // public double Fluency { get; set; }
    // public double Accuracy { get; set; }
    [JsonIgnore] public DateTime CreatedAt { get; set; }
    [JsonIgnore] public DateTime UpdatedAt { get; set; }

    [JsonIgnore]
    [NotMapped]
    public List<VocabularyList>? VocabularyLists { get; set; }
    
    public static List<VocabularyWord> ParseVocabularyWords(string vocabList, string delimiter = "comma")
    {
        string _delimiter = delimiter == "tab" ? "\t" : ",";

        List<VocabularyWord> vocabWords = new List<VocabularyWord>();
        char lineBreak = '\n'; // Simplified for cross-platform compatibility
        string[] lines = vocabList.Split(lineBreak);

        foreach (var line in lines)
        {
            string[] parts = line.Split(_delimiter);
            if (parts.Length == 2)
            {
                vocabWords.Add(new VocabularyWord
                {
                    TargetLanguageTerm  = parts[0].Replace("\"", ""), 
                    NativeLanguageTerm = parts[1].Replace("\"", "")
                });
            }
        }
        return vocabWords;
    }
}
