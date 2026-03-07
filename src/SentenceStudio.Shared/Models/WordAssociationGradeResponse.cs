using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// AI response for batch-grading Word Association clues
/// </summary>
public class WordAssociationGradeResponse
{
    [JsonPropertyName("entries")]
    public List<ClueGrade> Entries { get; set; } = new();
}

/// <summary>
/// Grade result for a single user-submitted clue
/// </summary>
public class ClueGrade
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("related")]
    public bool Related { get; set; }

    [JsonPropertyName("is_cloze")]
    public bool IsCloze { get; set; }

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("corrected_text")]
    public string? CorrectedText { get; set; }
}
