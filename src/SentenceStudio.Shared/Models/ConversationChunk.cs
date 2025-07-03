using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

public partial class ConversationChunk : ObservableObject
{
    // ID property for CoreSync (Entity Framework/Database)
    public int ID { get; set; }
    
    // Original properties with ObservableProperty for UI binding
    [ObservableProperty]
    private string? _text;

    [ObservableProperty]
    private double _comprehension;

    [ObservableProperty]
    private string? _comprehensionNotes;

    // Other properties
    public DateTime SentTime { get; set; }
    public string? Author { get; set; }
    public int ConversationId { get; set; }
    
    // Additional properties for CoreSync compatibility
    public int ConversationID 
    { 
        get => ConversationId; 
        set => ConversationId = value; 
    }
    
    public string? Content 
    { 
        get => Text; 
        set => Text = value; 
    }
    
    public string? Participant 
    { 
        get => Author; 
        set => Author = value; 
    }
    
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
}
