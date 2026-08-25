using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// One append-only audit record for a learner-owned write, holding safe metadata only.
/// </summary>
/// <remarks>
/// <para>
/// Every column on this entity is deliberately a bounded identifier, enum, timestamp, or
/// content-free code. There is no payload column, protected or otherwise, and there never should
/// be: the point of a separate table is that a reviewer can read its shape and be certain no
/// learner text, transcript, vocabulary term, prompt, secret, email, or confirmation material can
/// reach it, without having to trust that a redaction routine was applied correctly at every call
/// site. <c>CoachWriteAuditShapeTests</c> fails the build if a payload-shaped column is added.
/// </para>
/// <para>
/// Rows are written for refusals too. An audit that only records what succeeded cannot answer the
/// question an operator actually has, which is whether anything tried and was stopped.
/// </para>
/// </remarks>
public sealed class CoachWriteAudit
{
    /// <summary>Server-assigned identifier for this audit row.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The operation this record describes.</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>The owning learner.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Reserved for a future tenant boundary.</summary>
    public string? TenantId { get; set; }

    /// <summary>The conversation the operation belongs to.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The turn that produced the operation, when one is known.</summary>
    public string? TurnId { get; set; }

    /// <summary>The registered tool name.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>The registered risk class.</summary>
    public CoachToolRiskClass RiskClass { get; set; }

    /// <summary>What happened.</summary>
    public CoachWriteAuditEvent Event { get; set; }

    /// <summary>The kind of entity involved.</summary>
    public CoachWriteEntityKind EntityKind { get; set; } = CoachWriteEntityKind.None;

    /// <summary>The affected entity identifier, when one exists.</summary>
    public string? EntityId { get; set; }

    /// <summary>
    /// A closed-vocabulary reason code for a refusal. Never a message, never an exception string,
    /// never anything derived from learner input — those carry prompt and transcript fragments.
    /// </summary>
    public string? FailureCode { get; set; }

    /// <summary>When the event happened.</summary>
    public DateTime CreatedAtUtc { get; set; }
}
