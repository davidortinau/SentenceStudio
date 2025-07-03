using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("Conversations")]
public partial class Conversation : ObservableObject
{
    // Id property for CoreSync (Entity Framework/Database)
    public int Id { get; set; }
    
    // Original properties
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    // Navigation property for EF Core and CoreSync
    [NotMapped]
    public List<ConversationChunk>? Chunks { get; set; }

    // Constructors
    public Conversation() { }
    
    public Conversation(DateTime createdAt)
    {
        CreatedAt = createdAt;
        Chunks = new List<ConversationChunk>();
    }
}
