using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("UserActivities")]
public class UserActivity
{
    public int Id { get; set; }
    public string? Activity { get; set; }
    public string? Input {get; set;}
    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    [NotMapped]
    public bool IsCorrect => Accuracy >= 80;
}
