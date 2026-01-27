using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace SentenceStudio.Shared.Models;

[Table("ConversationChunks")]
public partial class ConversationChunk : ObservableObject
{
    // Id property for CoreSync (Entity Framework/Database)
    public int Id { get; set; }
    
    // Original properties with ObservableProperty for UI binding
    [ObservableProperty]
    [NotMapped]
    private string? _text;

    [ObservableProperty]
    [NotMapped]
    private double _comprehension;

    [ObservableProperty]
    [NotMapped]
    private string? _comprehensionNotes;

    // Other properties
    public DateTime SentTime { get; set; }
    public string? Author { get; set; }
    public int ConversationId { get; set; }
    
    /// <summary>
    /// The role of this message (User or Assistant).
    /// </summary>
    public ConversationRole Role { get; set; }

    /// <summary>
    /// JSON-serialized grammar corrections for user messages.
    /// </summary>
    public string? GrammarCorrectionsJson { get; set; }

    /// <summary>
    /// Deserialized grammar corrections (not mapped to database).
    /// </summary>
    [NotMapped]
    public List<GrammarCorrectionDto> GrammarCorrections
    {
        get
        {
            if (string.IsNullOrEmpty(GrammarCorrectionsJson))
                return new List<GrammarCorrectionDto>();
            try
            {
                return JsonSerializer.Deserialize<List<GrammarCorrectionDto>>(GrammarCorrectionsJson) ?? new();
            }
            catch
            {
                return new List<GrammarCorrectionDto>();
            }
        }
        set
        {
            GrammarCorrectionsJson = value?.Any() == true
                ? JsonSerializer.Serialize(value)
                : null;
        }
    }
    
    // Additional properties for CoreSync compatibility
    [NotMapped]
    public int ConversationID 
    { 
        get => ConversationId; 
        set => ConversationId = value; 
    }
    
    [NotMapped]
    public string? Content 
    { 
        get => Text; 
        set => Text = value; 
    }
    
    [NotMapped]
    public string? Participant 
    { 
        get => Author; 
        set => Author = value; 
    }
    
    [NotMapped]
    public DateTime CreatedAt 
    { 
        get => SentTime; 
        set => SentTime = value; 
    }

    // Constructors
    public ConversationChunk() { }
    
    public ConversationChunk(int conversationId, DateTime sentTime, string author, string text)
    {
        ConversationId = conversationId;
        Text = text;
        SentTime = sentTime;
        Author = author;
    }
    
    public ConversationChunk(int conversationId, DateTime sentTime, string author, string text, ConversationRole role)
    {
        ConversationId = conversationId;
        Text = text;
        SentTime = sentTime;
        Author = author;
        Role = role;
    }
}
