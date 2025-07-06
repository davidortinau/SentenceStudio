using System;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

public class Lesson
{
    public string? Topic { get; set; }
    public List<Sentence>? Sentences { get; set; }
    public List<string>? Vocabulary { get; set; }
    public decimal Fluency { get; set; }
    public decimal Accuracy { get; set; }
    public DateTime LastAnsweredAt { get; set; }
}
