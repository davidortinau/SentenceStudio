using System;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

public partial class Conversation
{
    public int ID { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<ConversationChunk>? Chunks { get; set; }
}
