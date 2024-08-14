

namespace SentenceStudio.Models;

public partial class Sentence : ObservableObject
{
    [ObservableProperty]
    private string _problem;

    [ObservableProperty]
    private string _answer;

    [ObservableProperty]
    private double _accuracy;
    
    [ObservableProperty]
    private string _accuracyExplanation;

    [ObservableProperty]
    private double _fluency;

    [ObservableProperty]
    private string _fluencyExplanation;
    
    [ObservableProperty]
    private string _recommendedSentence;
    
    public string GrammarNotes { get; set; }
    public List<string> Vocabulary { get; set; }

    

}
