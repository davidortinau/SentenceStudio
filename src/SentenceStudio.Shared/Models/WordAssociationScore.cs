using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Tracks high scores for Word Association activity rounds
/// </summary>
[Table("WordAssociationScores")]
public class WordAssociationScore
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Total points scored in the round (related clues = 1pt, related cloze = 2pt)
    /// </summary>
    public int RoundScore { get; set; }

    /// <summary>
    /// Total number of clues submitted across all words
    /// </summary>
    public int TotalClues { get; set; }

    /// <summary>
    /// Number of vocabulary words in the round (typically 5)
    /// </summary>
    public int WordCount { get; set; } = 5;

    /// <summary>
    /// Comma-separated vocabulary word IDs used in this round
    /// </summary>
    public string? WordIds { get; set; }

    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
