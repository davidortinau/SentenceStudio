namespace SentenceStudio;

public class Sentence
{
    public string Problem { get; set; }
    public string Answer { get; set; }
    public decimal Accuracy { get; set; }
    public decimal Fluency { get; set; }
    public string Explanation { get; set; }
    public List<string> Vocabulary { get; set; }
}
