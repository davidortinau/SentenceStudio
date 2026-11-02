using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class Reply
{
    [Description("The conversation partner's in-character reply only, in the target language. Pure dialogue: never include a comprehension score, rating, percentage, grade, or notes about the user's language here.")]
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [Description("Comprehension score as a whole number from 0 to 100 (0 = understood nothing, 100 = understood everything) for how well the user's last message was understood. The score belongs only in this field, never in the message.")]
    [JsonPropertyName("comprehension_score")]
    public double Comprehension { get; set; }

    [Description("Notes about the user's comprehension and communication effectiveness")]
    [JsonPropertyName("comprehension_notes")]
    public string? ComprehensionNotes { get; set; }

    [Description("List of grammar corrections found in the user's input")]
    [JsonPropertyName("grammar_corrections")]
    public List<GrammarCorrectionDto> GrammarCorrections { get; set; } = new();

    [Description("Per-word vocabulary analysis of the user's message")]
    [JsonPropertyName("vocabulary_analysis")]
    public List<VocabularyAnalysis>? VocabularyAnalysis { get; set; }

    /// <summary>
    /// Comprehension is shown as a 0-100 percentage. Until 2026-11 the schema
    /// description asked the model for 0.0-1.0 while the prompts asked for
    /// 0-100, so replies (and chunks stored from them) can hold either scale.
    /// A positive value of at most 1 is read as a fraction; everything is
    /// clamped to 0-100 and rounded to a whole percentage.
    /// </summary>
    public static double NormalizeComprehension(double score)
    {
        if (double.IsNaN(score) || score <= 0)
            return 0;
        if (score <= 1)
            score *= 100;
        return Math.Round(Math.Min(score, 100), MidpointRounding.AwayFromZero);
    }
}
