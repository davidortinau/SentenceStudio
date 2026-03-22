using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("UserActivities")]
public class UserActivity
{
    public string Id { get; set; } = string.Empty;
    public string? Activity { get; set; }
    public string? Input {get; set;}
    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public string UserProfileId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    [NotMapped]
    public bool IsCorrect => Accuracy >= 80;
}
