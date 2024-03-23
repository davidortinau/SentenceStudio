using System;

namespace SentenceStudio.Models;

public class ConversationChunk
{
    public ConversationChunk(DateTime sentTime, ConversationParticipant author, string text)
    {
        Text = text;
        SentTime = sentTime;
        Author = author;
    }

    public string Text { get; }

    public DateTime SentTime { get; }

    public ConversationParticipant Author { get; }
}
