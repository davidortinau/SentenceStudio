using System;

namespace SentenceStudio.Models;

public partial class ConversationChunk : ObservableObject
{
    public ConversationChunk()
    {
        
    }
    public ConversationChunk(int conversationId, DateTime sentTime, string author, string text)
    {
        ConversationId = conversationId;
        Text = text;
        SentTime = sentTime;
        Author = author;
    }

    [ObservableProperty]
    private string _text;

    public DateTime SentTime { get; set;}

    public string Author { get;set; }

    [ObservableProperty]
    private double _comprehension;

    [ObservableProperty]
    private string _comprehensionNotes;

    public int ConversationId { get; set; } 
}
