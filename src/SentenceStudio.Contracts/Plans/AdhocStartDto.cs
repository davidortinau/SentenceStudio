using System.Text.Json.Serialization;

namespace SentenceStudio.Contracts.Plans;

/// <summary>
/// Wire request for <c>POST /api/v1/plans/adhoc/start</c>. The <c>ClientSessionId</c>
/// is the idempotency key — repeat calls with the same id resolve to the same
/// <c>PlanItemId</c> and never create duplicate <c>DailyPlanCompletion</c> rows.
/// </summary>
public sealed record AdhocStartRequest
{
    /// <summary>Client-generated UUID. Used as the idempotency key per user.</summary>
    [JsonPropertyName("clientSessionId")]
    public string? ClientSessionId { get; init; }

    /// <summary>PascalCase enum string — see <see cref="PlanActivityTypes"/>.</summary>
    [JsonPropertyName("activityType")]
    public string? ActivityType { get; init; }

    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; init; }

    [JsonPropertyName("skillId")]
    public string? SkillId { get; init; }

    /// <summary>Optional. Defaults to 10 when omitted.</summary>
    [JsonPropertyName("estimatedMinutes")]
    public int? EstimatedMinutes { get; init; }
}

/// <summary>
/// Wire response for <c>POST /api/v1/plans/adhoc/start</c>.
/// </summary>
public sealed record AdhocStartResponse
{
    /// <summary>Synthetic id prefixed with <c>adhoc-</c>. Accepted by the existing
    /// <c>POST /api/v1/plans/{date}/items/{id}/progress</c> and
    /// <c>POST /api/v1/plans/{date}/items/{id}/complete</c> endpoints.</summary>
    [JsonPropertyName("planItemId")]
    public required string PlanItemId { get; init; }

    [JsonPropertyName("activityType")]
    public required string ActivityType { get; init; }

    /// <summary>Calendar date the completion row is attached to, <c>yyyy-MM-dd</c> UTC.</summary>
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("estimatedMinutes")]
    public int EstimatedMinutes { get; init; }
}
