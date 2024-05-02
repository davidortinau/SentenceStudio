using SQLite;

namespace SentenceStudio.Models;

public class Term
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string TargetLanguageTerm { get; set; }
    public string NativeLanguageTerm { get; set; }
    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int VocabularyListId { get; set; } 

    public static List<Term> ParseTerms(string vocabList, string delimiter = "comma")
    {
        string _delimiter = delimiter == "tab" ? "\t" : ",";

        List<Term> terms = new List<Term>();
        char lineBreak = (DeviceInfo.Platform == DevicePlatform.WinUI) ? '\r' : '\n';
        //vocabList = vocabList.Replace("\r", "");
        string[] lines = vocabList.Split(lineBreak);


        foreach (var line in lines)
        {
            string[] parts = line.Split(_delimiter);
            if (parts.Length == 2)
            {
                terms.Add(new Term
                {
                    TargetLanguageTerm  = parts[0].Replace("\"", ""), 
                    NativeLanguageTerm = parts[1].Replace("\"", "")
                });
            }
        }
        return terms;
    }
    
}



