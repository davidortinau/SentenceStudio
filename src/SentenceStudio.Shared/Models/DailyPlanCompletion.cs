using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("DailyPlanCompletions")]
public class DailyPlanCompletion
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string PlanItemId { get; set; } = string.Empty;
    public string ActivityType { get; set; } = string.Empty;
    public int? ResourceId { get; set; }
    public int? SkillId { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    /// <summary>
    /// Actual minutes spent on this activity. Updated incrementally as user works.
    /// Can exceed EstimatedMinutes if user takes longer.
    /// </summary>
    public int MinutesSpent { get; set; }
    
    // New fields for plan reconstruction
    public int EstimatedMinutes { get; set; }
    public int Priority { get; set; }
    public string TitleKey { get; set; } = string.Empty;
    public string DescriptionKey { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string RouteParametersJson { get; set; } = string.Empty; // JSON serialized Dictionary<string,object>
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
