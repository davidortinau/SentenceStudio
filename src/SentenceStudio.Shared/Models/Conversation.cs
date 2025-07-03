using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

public partial class Conversation : ObservableObject
{
    // ID property for CoreSync (Entity Framework/Database)
    public int ID { get; set; }
    
    // Original properties
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    // Navigation property for EF Core and CoreSync
    public List<ConversationChunk>? Chunks { get; set; }

    // Constructors
    public Conversation() { }
    
    public Conversation(DateTime createdAt)
    {
        CreatedAt = createdAt;
        Chunks = new List<ConversationChunk>();
    }
}
