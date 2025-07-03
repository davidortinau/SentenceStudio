using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyLists")]
public class VocabularyList
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    [NotMapped]
    public List<VocabularyWord>? Words { get; set; }
    
    public override string ToString() => Name ?? string.Empty;
}
