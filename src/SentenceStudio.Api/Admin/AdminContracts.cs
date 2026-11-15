namespace SentenceStudio.Api.Admin;

/// <summary>Who the admin portal is talking to, and whether their password-again proof is live.</summary>
public sealed record AdminSessionResponse(
    string UserId,
    string? Email,
    string? DisplayName,
    DateTimeOffset? StepUpExpiresAtUtc);

/// <summary>The body of <c>POST /api/admin/step-up</c>.</summary>
public sealed record AdminStepUpRequest(string? Password);

/// <summary>The proof to send back in <c>X-Admin-Proof</c>, and when it stops working.</summary>
public sealed record AdminStepUpResponse(string Proof, DateTimeOffset ExpiresAtUtc);

/// <summary>One audit row as the Audit page shows it.</summary>
/// <param name="ActorKind">A code from <see cref="AdminAuditWire"/>: <c>admin</c> or <c>agent_token</c>.</param>
/// <param name="ActorEmail">
/// Looked up for display when the actor is an account that still exists. The audit table itself
/// stores only the id.
/// </param>
/// <param name="Outcome">A code from <see cref="AdminAuditWire"/>: <c>started</c>, <c>succeeded</c> or <c>failed</c>.</param>
public sealed record AdminAuditRow(
    long Id,
    DateTime OccurredAtUtc,
    string ActorKind,
    string ActorId,
    string? ActorEmail,
    string Action,
    string? TargetKind,
    string? TargetId,
    string Outcome,
    int? StatusCode,
    string? FailureCode);

/// <summary>One page of audit rows, newest first.</summary>
/// <param name="NextBefore">Pass as <c>before</c> to read the next older page; null at the end.</param>
public sealed record AdminAuditPage(IReadOnlyList<AdminAuditRow> Items, long? NextBefore);
