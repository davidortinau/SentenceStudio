using System;

namespace SentenceStudio.Models;

[ObservableObject]
public partial class ConversationChunk
{
    public ConversationChunk()
    {
        
    }
    public ConversationChunk(DateTime sentTime, string author, string text)
    {
        Text = text;
        SentTime = sentTime;
        Author = author;
    }

    [ObservableProperty]
    private string _text;

    public DateTime SentTime { get; set;}

    public string Author { get;set; }
}
