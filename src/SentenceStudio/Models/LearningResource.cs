using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;
using SQLiteNetExtensions.Attributes;

namespace SentenceStudio.Models;

public class LearningResource
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }

    public string Title { get; set; }

    public string Description { get; set; }

    // The type of media (podcast, video, image, vocabulary list, etc.)
    public string MediaType { get; set; }

    // URL to the media content (if applicable)
    public string MediaUrl { get; set; }

    // Original text content/transcript
    public string Transcript { get; set; }

    // Optional translated text
    public string Translation { get; set; }

    // Language of the content (e.g. "Korean", "Spanish")
    public string Language { get; set; }

    // Skills related to this resource
    public int? SkillID { get; set; }

    // For compatibility with existing VocabularyList
    public int? OldVocabularyListID { get; set; }

    // Tags for easier filtering
    public string Tags { get; set; }

    [JsonIgnore]
    public DateTime CreatedAt { get; set; }

    [JsonIgnore]
    public DateTime UpdatedAt { get; set; }

    // The vocabulary words associated with this resource
    [Ignore]
    public List<VocabularyWord> Vocabulary { get; set; } = new List<VocabularyWord>();

    // Helper property to determine if this is a vocabulary list
    [Ignore]
    public bool IsVocabularyList => MediaType == "Vocabulary List";

    
}

// Mapping table to connect resources and vocabulary words (many-to-many relationship)
public class ResourceVocabularyMapping
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    
    [Indexed]
    public int ResourceID { get; set; }
    
    [Indexed]
    public int VocabularyWordID { get; set; }
}