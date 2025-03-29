using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;

namespace SentenceStudio.Models;
public partial class Challenge : INotifyPropertyChanged
{
    [JsonIgnore]
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public string SentenceText { get; set; }
    public string RecommendedTranslation { get; set; }
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; }
    
    [JsonIgnore]
    public DateTime UpdatedAt { get; set; }
    
    [Description("Includes all the English words to complete the full sentence, but only include the Korean if the word is necessary for a natural Korean sentence. Use the Korean dictionary form in the vocabulary array.")]
    [Ignore] 
    public List<VocabularyWord> Vocabulary { get; set; }
    
    public string VocabularyWord {get;set;}
    public string VocabularyWordAsUsed { get; set; }
    
    [Description("Five comma separated words from which the user can choose, including the correct vocabulary word.")]
    public string VocabularyWordGuesses { get; set; }
    
    [JsonIgnore]
    private UserActivity _userActivity;

    [JsonIgnore]
    [Ignore] 
    public UserActivity UserActivity
    {
        get{
            return _userActivity;
        }
        set {
            if (value != null)
            {
                _userActivity = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserActivity)));
            }
        }
    }

    [JsonIgnore]
    private bool isCurrent;

    [JsonIgnore]
    [Ignore]
    public bool IsCurrent
    {
        get => isCurrent; internal set
        {
            isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}