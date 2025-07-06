using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

public partial class Sentence : ObservableObject
{
    // Observable properties for UI binding
    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string? _answer;

    [ObservableProperty]
    private double _accuracy;
    
    [ObservableProperty]
    private string? _accuracyExplanation;

    [ObservableProperty]
    private double _fluency;

    [ObservableProperty]
    private string? _fluencyExplanation;
    
    [ObservableProperty]
    private string? _recommendedSentence;
    
    // Other properties
    public string? GrammarNotes { get; set; }
    public List<string>? Vocabulary { get; set; }

    // Constructors
    public Sentence() { }
    
    public Sentence(string problem)
    {
        Problem = problem;
        Vocabulary = new List<string>();
    }
}
