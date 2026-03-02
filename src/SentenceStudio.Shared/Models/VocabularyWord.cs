using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

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

    // === NEW ENCODING FIELDS ===
    
    [Description("The dictionary form or lemma of the word, for grouping inflected forms")]
    [ObservableProperty]
    private string? lemma;
    
    [Description("Comma-separated tags for categorizing vocabulary words")]
    [ObservableProperty]
    private string? tags;
    
    [Description("Silly story or memory association to aid recall")]
    [ObservableProperty]
    private string? mnemonicText;
    
    [Description("Image URL to visualize the mnemonic or concept")]
    [ObservableProperty]
    private string? mnemonicImageUri;
    
    [ObservableProperty]
    private string? audioPronunciationUri;

    [JsonIgnore]
    [NotMapped]
    public List<VocabularyList>? VocabularyLists { get; set; }
    
    // Navigation properties for many-to-many with LearningResource
    [JsonIgnore]
    public List<LearningResource> LearningResources { get; set; } = new List<LearningResource>();
    
    [JsonIgnore]
    public List<ResourceVocabularyMapping> ResourceMappings { get; set; } = new List<ResourceVocabularyMapping>();
    
    // Navigation property for example sentences
    [JsonIgnore]
    public List<ExampleSentence> ExampleSentences { get; set; } = new List<ExampleSentence>();
    
    // === DERIVED PROPERTIES (Not Mapped) ===
    
    [NotMapped]
    public double EncodingStrength { get; set; }
    
    [NotMapped]
    public string EncodingStrengthLabel { get; set; } = "Basic";
    
    public static List<VocabularyWord> ParseVocabularyWords(string vocabList, string delimiter = "comma")
    {
        if (string.IsNullOrWhiteSpace(vocabList))
            return new List<VocabularyWord>();

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
