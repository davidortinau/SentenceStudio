using System;

namespace SentenceStudio.Shared.Models;

public class StreamHistory
{
    public int ID { get; set; }
    public string? Phrase { get; set; }
    public double Duration { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? FileName { get; set; }
    public string? Title { get; set; }
    public string? Source { get; set; }
    public string? SourceUrl { get; set; }
    public string? VoiceId { get; set; }
    public string? AudioFilePath { get; set; }
}
