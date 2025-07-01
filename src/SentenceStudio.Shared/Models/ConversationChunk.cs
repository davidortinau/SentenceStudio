namespace SentenceStudio.Shared.Models;

public class ConversationChunk
{
    public int ID { get; set; }
    public int ConversationID { get; set; }
    public string? Content { get; set; }
    public string? Participant { get; set; }
    public DateTime CreatedAt { get; set; }
}
