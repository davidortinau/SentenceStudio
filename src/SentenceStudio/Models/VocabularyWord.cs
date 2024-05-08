using System.Text.Json.Serialization;
using SQLite;
using SQLiteNetExtensions.Attributes;

namespace SentenceStudio.Models;
public class VocabularyWord
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }

    [JsonPropertyName("original")]
    public string NativeLanguageTerm { get; set; }

    [JsonPropertyName("translation")]
    public string TargetLanguageTerm { get; set; } 

    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

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