using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Join entity linking phrase words to their constituent words.
/// Represents the "built from" relationship — e.g., "나는" is built from "나" and "는".
/// </summary>
[Table("PhraseConstituent")]
public class PhraseConstituent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// FK to the phrase word (the composite unit).
    /// </summary>
    public string PhraseWordId { get; set; } = string.Empty;
    
    /// <summary>
    /// FK to the constituent word (a component of the phrase).
    /// Nullable to support SetNull on delete.
    /// </summary>
    public string? ConstituentWordId { get; set; }
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    [JsonIgnore]
    [ForeignKey(nameof(PhraseWordId))]
    public VocabularyWord? PhraseWord { get; set; }
    
    [JsonIgnore]
    [ForeignKey(nameof(ConstituentWordId))]
    public VocabularyWord? ConstituentWord { get; set; }
}
