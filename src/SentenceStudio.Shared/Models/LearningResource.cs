using System.ComponentModel;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

[Table("LearningResources")]
public partial class LearningResource : ObservableObject
{
    public int Id { get; set; }

    [ObservableProperty]
    private string? title;

    [ObservableProperty]
    private string? description;

    // The type of media (podcast, video, image, vocabulary list, etc.)
    [ObservableProperty]
    private string? mediaType;

    // URL to the media content (if applicable)
    [ObservableProperty]
    private string? mediaUrl;

    // Original text content/transcript
    [ObservableProperty]
    private string? transcript;

    // Optional translated text
    [ObservableProperty]
    private string? translation;

    // Language of the content (e.g. "Korean", "Spanish")
    [ObservableProperty]
    private string? language;

    // Skills related to this resource
    public int? SkillID { get; set; }

    // For compatibility with existing VocabularyList
    public int? OldVocabularyListID { get; set; }

    // Tags for easier filtering
    [ObservableProperty]
    private string? tags;

    [JsonIgnore]
    public DateTime CreatedAt { get; set; }

    [JsonIgnore]
    public DateTime UpdatedAt { get; set; }

    // The vocabulary words associated with this resource
    [NotMapped]
    public List<VocabularyWord> Vocabulary { get; set; } = new List<VocabularyWord>();

    // Helper property to determine if this is a vocabulary list
    [NotMapped]
    public bool IsVocabularyList => MediaType == "Vocabulary List";
}
