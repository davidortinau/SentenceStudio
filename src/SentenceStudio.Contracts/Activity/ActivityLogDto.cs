using System.Text.Json.Serialization;

namespace SentenceStudio.Contracts.Activity;

/// <summary>
/// Wire contract for <c>GET /api/v1/activity-log</c>. Mirrors the shape the
/// Flutter client builds its UI around (see activity-log-api-spec.md). The
/// server maps from <c>SentenceStudio.Services.Progress.ActivityLogWeek</c>
/// to this DTO so the wire contract stays explicit and stable even as the
/// internal model evolves.
/// </summary>
public sealed record ActivityLogWeekDto
{
    [JsonPropertyName("weekStart")]
    public DateTime WeekStart { get; init; }

    [JsonPropertyName("weekEnd")]
    public DateTime WeekEnd { get; init; }

    [JsonPropertyName("days")]
    public required IReadOnlyList<ActivityLogDayDto> Days { get; init; }
}

public sealed record ActivityLogDayDto
{
    /// <summary>Calendar date as <c>yyyy-MM-dd</c>. No time component — Flutter
    /// treats this as the user's local day.</summary>
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("hasActivity")]
    public bool HasActivity { get; init; }

    [JsonPropertyName("inputMinutes")]
    public int InputMinutes { get; init; }

    [JsonPropertyName("outputMinutes")]
    public int OutputMinutes { get; init; }

    [JsonPropertyName("totalMinutes")]
    public int TotalMinutes { get; init; }

    [JsonPropertyName("allPlansCompleted")]
    public bool AllPlansCompleted { get; init; }

    [JsonPropertyName("plans")]
    public required IReadOnlyList<ActivityLogPlanDto> Plans { get; init; }
}

public sealed record ActivityLogPlanDto
{
    [JsonPropertyName("isAdhoc")]
    public bool IsAdhoc { get; init; }

    /// <summary>Stable identifier for the plan grouping. For ad-hoc plans this
    /// is the first entry's PlanItemId (already prefixed <c>adhoc-</c>). For
    /// generated plans it is the first entry's PlanItemId in the cluster.</summary>
    [JsonPropertyName("planItemId")]
    public string PlanItemId { get; init; } = string.Empty;

    /// <summary>Human-readable label for the plan grouping. Generated plans are
    /// labeled <c>Plan N</c> ordered chronologically within the day. Ad-hoc
    /// plans are labeled <c>Ad-hoc</c>.</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("totalMinutes")]
    public int TotalMinutes { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<ActivityLogEntryDto> Entries { get; init; }
}

public sealed record ActivityLogEntryDto
{
    /// <summary>PascalCase enum name — e.g. <c>Reading</c>, <c>Writing</c>,
    /// <c>VocabularyReview</c>. Matches the <c>PlanActivityType</c> enum.</summary>
    [JsonPropertyName("activityType")]
    public required string ActivityType { get; init; }

    /// <summary>Either <c>"Input"</c> or <c>"Output"</c> — PascalCase, matching
    /// <c>ActivityCategory</c>.</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("minutesSpent")]
    public int MinutesSpent { get; init; }

    /// <summary>ISO 8601 UTC timestamp with <c>Z</c> suffix. Null when the
    /// activity is in-progress (i.e. <c>IsCompleted=false</c>).</summary>
    [JsonPropertyName("completedAtUtc")]
    public DateTime? CompletedAtUtc { get; init; }
}
