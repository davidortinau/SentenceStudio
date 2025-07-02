namespace SentenceStudio.Shared.Models;

public partial class Sentence
{
    public string? Problem { get; set; }
    public string? Answer { get; set; }
    public double Accuracy { get; set; }
    public string? AccuracyExplanation { get; set; }
    public double Fluency { get; set; }
    public string? FluencyExplanation { get; set; }
    public string? RecommendedSentence { get; set; }
    public string? GrammarNotes { get; set; }
    public List<string>? Vocabulary { get; set; }
}
