using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;
using SQLiteNetExtensions.Attributes;

namespace SentenceStudio.Models;
public class VocabularyWord
{
    [JsonIgnore]
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }

    [Description("The word in the user's native language, usually English.")]
    public string NativeLanguageTerm { get; set; }

    [Description("The word in the language being learned, usually Korean.")]
    public string TargetLanguageTerm { get; set; } 

    // public double Fluency { get; set; }
    // public double Accuracy { get; set; }
    [JsonIgnore] public DateTime CreatedAt { get; set; }
    [JsonIgnore] public DateTime UpdatedAt { get; set; }

    [JsonIgnore]
    [ManyToMany(typeof(VocabularyWord))]
    public List<VocabularyList> VocabularyLists { get; set; }
    
    public static List<VocabularyWord> ParseVocabularyWords(string vocabList, string delimiter = "comma")
    {
        string _delimiter = delimiter == "tab" ? "\t" : ",";

        List<VocabularyWord> vocabWords = new List<VocabularyWord>();
        char lineBreak = (DeviceInfo.Platform == DevicePlatform.WinUI) ? '\r' : '\n';
        //vocabList = vocabList.Replace("\r", "");
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