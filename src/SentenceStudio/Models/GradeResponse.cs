using System.Text.Json.Serialization;

namespace SentenceStudio.Models;
public class GradeResponse
    {
        [JsonPropertyName("fluency_score")]
        public double Fluency { get; set; }

        [JsonPropertyName("fluency_explanation")]
        public string FluencyExplanation { get; set; }

        [JsonPropertyName("accuracy_score")]
        public double Accuracy { get; set; }

        [JsonPropertyName("accuracy_explanation")]
        public string AccuracyExplanation { get; set; }

        [JsonPropertyName("recommended_translation")]
        public string RecommendedTranslation { get; set; }

        [JsonPropertyName("grammar_notes")]
        public GrammarNotes GrammarNotes { get; set; }
    }