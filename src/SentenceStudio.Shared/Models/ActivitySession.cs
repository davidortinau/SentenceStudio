namespace SentenceStudio.Shared.Models;

public enum ActivitySessionStatus
{
    InProgress,
    Completed,
    Abandoned
}

public class ActivitySession
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ActivityType { get; set; } = string.Empty;
    public string LaunchContextKey { get; set; } = string.Empty;
    public string StateJson { get; set; } = string.Empty;
    public ActivitySessionStatus Status { get; set; } = ActivitySessionStatus.InProgress;
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
