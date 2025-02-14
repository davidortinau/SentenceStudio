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
    
    [JsonIgnore]
    [Ignore] 
    public List<VocabularyWord> Vocabulary { get; set; }
    
    public string VocabularyWord {get;set;}
    public string VocabularyWordAsUsed { get; set; }
    
    [JsonIgnore]
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