using System;

namespace SentenceStudio.Shared.Models;

public class UserActivity
{
    public int ID { get; set; }
    public string? Activity { get; set; }
    public string? Input {get; set;}
    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsCorrect => Accuracy >= 80;
}
