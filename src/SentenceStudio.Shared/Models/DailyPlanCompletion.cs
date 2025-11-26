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
    
    // Fields for plan reconstruction
    public int EstimatedMinutes { get; set; }
    public int Priority { get; set; }
    public string TitleKey { get; set; } = string.Empty;
    public string DescriptionKey { get; set; } = string.Empty;

    /// <summary>
    /// The LLM-generated rationale explaining why this plan was created.
    /// Stored redundantly in all items for the same date for easy reconstruction.
    /// </summary>
    public string Rationale { get; set; } = string.Empty;
    
    // NOTE: Route and RouteParameters are NOT stored - they're derived from ActivityType, 
    // ResourceId, and SkillId during reconstruction using PlanConverter logic.
    // This ensures route logic stays in one place and adapts to future changes.

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
