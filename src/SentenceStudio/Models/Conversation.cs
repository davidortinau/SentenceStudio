using System;
using SQLite;

namespace SentenceStudio.Models;

public partial class Conversation : ObservableObject
{
    public Conversation()
    {
        
    }
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Ignore]
    public List<ConversationChunk> Chunks { get; set; }
    
}
