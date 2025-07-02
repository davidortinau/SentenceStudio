using System;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

public class VocabularyList
{
    public int ID { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<VocabularyWord>? Words { get; set; }
    public override string ToString() => Name ?? string.Empty;
}
