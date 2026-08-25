using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Reports;

/// <summary>
/// One learner's report that one coach response did not serve them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every column is a bounded identifier, an enum ordinal, a closed-vocabulary code, a
/// sequence number, a count, or a timestamp.</b> There is no payload column, protected or
/// otherwise, and no free-text column — the same rule <c>CoachWriteAudit</c> and
/// <c>CoachOpportunity</c> follow, and for the same reason: a reviewer can establish by reading
/// this declaration that no learner text, model completion, tool argument, or reviewer prose can
/// reach the table, without having to trust that a redaction routine ran at every call site.
/// <c>CoachResponseReportShapeTests</c> fails the build if a text-shaped column appears.
/// </para>
/// <para>
/// What the learner asked and what the coach answered survive only inside the two encrypted
/// <c>CoachMessage</c> rows that <see cref="RequestMessageId"/> and
/// <see cref="CoachMessageId"/> point at. Reading them is a separate, explicit, self-auditing
/// operator action on the ledger row this report produced.
/// </para>
/// <para>
/// <b>Why this is not simply a row on <c>CoachOpportunity</c>.</b> The ledger's identity is a
/// <em>problem</em>: one row per learner, per fingerprint, per UTC day, so
/// <c>GROUP BY Fingerprint</c> answers "how many people hit this". A report has a second identity
/// the ledger cannot express — a specific response, forever — and that is the identity the
/// learner-facing state ("Reported for review", surviving a reload) and the idempotency guarantee
/// are both built on. Folding it into the fingerprint would have made every reported response its
/// own rollup group of one, silently changing what the daily rollup means for every existing
/// consumer. Two identities, two tables; the ledger row stays the product signal and this row
/// stays the per-response fact.
/// </para>
/// <para>
/// <b>No foreign key</b>, deliberately, and for the same reason <c>CoachWriteAudit</c> has none:
/// the row must survive a conversation that no longer exists, and evidence resolution therefore
/// fails closed — a pointer whose message no longer resolves reads as "unavailable", never as an
/// error.
/// </para>
/// <para>
/// PostgreSQL-only, on <c>CoachDbContext</c>. Coach state never syncs to a device, so this never
/// joins the CoreSync entity set and never produces a mobile SQLite migration.
/// </para>
/// </remarks>
public sealed class CoachResponseReport
{
    /// <summary>Server-assigned identifier for this row.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The owning learner. The only ownership authority — every query filters on this, and the
    /// unique index that makes reporting idempotent is rooted in it.
    /// </summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Reserved for a future tenant boundary. Never queried, never keyed.</summary>
    public string? TenantId { get; set; }

    /// <summary>The conversation the reported exchange lives in.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// The reported coach response. Half of the uniqueness key, so one response can be reported
    /// once by its owner and no more.
    /// </summary>
    public string CoachMessageId { get; set; } = string.Empty;

    /// <summary>Its immutable position in the conversation.</summary>
    public long CoachMessageSequence { get; set; }

    /// <summary>
    /// The learner's own message the reported response answered.
    /// </summary>
    /// <remarks>
    /// Required, and validated to sit in the same turn as the response before anything is
    /// written. A report whose exchange the server cannot pair authoritatively is refused rather
    /// than recorded against a guess: reporting the wrong exchange sends a reviewer to read a
    /// conversation the learner never complained about.
    /// </remarks>
    public string RequestMessageId { get; set; } = string.Empty;

    /// <summary>Its immutable position in the conversation.</summary>
    public long RequestMessageSequence { get; set; }

    /// <summary>Why the learner reported it. Stored as an ordinal.</summary>
    public CoachResponseReportReason Reason { get; set; }

    /// <summary>
    /// How the response rendered to the learner: an answer, a clarification, a notice.
    /// </summary>
    /// <remarks>
    /// The nearest content-free statement of what the model meant to do, taken from the ledger
    /// row's own classification rather than from anything the model said. Stored as an ordinal.
    /// </remarks>
    public CoachMessageKind ResponseKind { get; set; }

    /// <summary>The durable turn operation that produced the response, when one is recorded.</summary>
    public string? TurnOperationId { get; set; }

    /// <summary>The operation's durable state at the moment of the report. Stored as an ordinal.</summary>
    public CoachTurnOperationStatus? TurnStatus { get; set; }

    /// <summary>
    /// The turn's stop reason, read from the operation's own replayable outcome. Stored as an
    /// ordinal.
    /// </summary>
    /// <remarks>
    /// Extracted server-side from a payload the turn replay path already decrypts, and only the
    /// enum is kept. Nothing else from that payload is read, stored, returned, or logged.
    /// </remarks>
    public CoachStopReason? StopReason { get; set; }

    /// <summary>How many workers claimed the operation. A retried turn is worth a reviewer knowing.</summary>
    public int? TurnAttemptCount { get; set; }

    /// <summary>The operation's closed, content-free failure code, when it failed.</summary>
    public string? TurnErrorCode { get; set; }

    /// <summary>
    /// The registered tool names invoked on this turn, comma-separated and ordinally sorted.
    /// </summary>
    /// <remarks>
    /// <b>Every element is validated against <c>ICoachToolRegistry.IsRegistered</c> before it is
    /// written</b>, so this column is a bounded set of server-owned constants and not a free-text
    /// column wearing a list's clothes. A name the registry does not know is dropped, never
    /// stored. The set is read from the write ledger's audit rows, which are the server's own
    /// record of what ran.
    /// </remarks>
    public string? InvokedToolNames { get; set; }

    /// <summary>
    /// The write proposal this turn produced, when it produced one.
    /// </summary>
    /// <remarks>
    /// One identifier covers proposal, execution, and receipt: the write ledger addresses all
    /// three by the same operation id, and the receipt route is
    /// <c>/writes/{operationId}/receipt</c>. A second column would only restate it.
    /// </remarks>
    public string? WriteOperationId { get; set; }

    /// <summary>The proposal's state at the moment of the report. Stored as an ordinal.</summary>
    public CoachWriteOperationStatus? WriteStatus { get; set; }

    /// <summary>The write ledger's closed, content-free refusal code, when the write was refused.</summary>
    public string? WriteFailureCode { get; set; }

    /// <summary>
    /// The opportunity ledger row this report produced, when one was written.
    /// </summary>
    /// <remarks>
    /// Null is a normal value, not an error: the recorder is an observer and is allowed to be
    /// switched off, and a report is recorded either way. The report is the learner's action; the
    /// ledger row is the product signal it raised.
    /// </remarks>
    public string? OpportunityId { get; set; }

    /// <summary>When the report was recorded (UTC).</summary>
    public DateTime ReportedAtUtc { get; set; }

    /// <summary>The row-shape contract version this row was written under.</summary>
    public int SchemaVersion { get; set; } = CoachResponseReportLimits.SchemaVersion;

    // ─────────────────────────────────────────────────────────────────────────
    // Grounding evidence. Schema version 2.
    //
    // Every column is nullable, bounded, and a closed code or a count. Null is
    // the normal reading for three different situations that must stay
    // indistinguishable in the data: the ladder was Off, the row predates these
    // columns, or the stored outcome could not be read. None of the three is a
    // finding, and a zero in any of these columns would be read as one.
    //
    // Projected once, at report time, from the protected turn outcome's
    // Grounding section. Never from the request-scoped observation buffer: the
    // buffer belongs to the turn in flight, and a learner reporting a response
    // is a different request from the one that produced it.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The rung the disputed turn ran at, as a <c>CoachGroundingStage</c> ordinal.</summary>
    public int? GroundingStage { get; set; }

    /// <summary>Whether the ladder withheld the answer entirely.</summary>
    public bool? GroundingRefused { get; set; }

    /// <summary>Whether at least one span was substituted.</summary>
    public bool? GroundingAltered { get; set; }

    /// <summary>
    /// Whether substitution was permitted by the rung and held back because the repair copy is not
    /// in the learner's display language.
    /// </summary>
    /// <remarks>
    /// Its own column rather than an inference from <see cref="GroundingAltered"/> being false. An
    /// operator watching a Repair rollout needs to tell "nothing needed fixing" from "everything
    /// needed fixing and none of it could be said in the learner's language", and those two produce
    /// the same value in every other column.
    /// </remarks>
    public bool? GroundingRepairSuppressed { get; set; }

    /// <summary>How many findings the ladder recorded.</summary>
    public int? GroundingFindingCount { get; set; }

    /// <summary>
    /// The distinct rule codes that fired, by name, ordinal-sorted and comma-joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Names rather than ordinals, because a report row outlives the build that wrote it and an
    /// ordinal is only meaningful beside the enum it came from. Sorted so two reports of the same
    /// shape produce the same string and a rollup can group on it.
    /// </para>
    /// <para>
    /// An unrecognised code is dropped whole. Never truncated, never abbreviated: a partial name
    /// decodes to nothing and a reader cannot tell it from a real one.
    /// </para>
    /// </remarks>
    public string? GroundingRuleCodes { get; set; }

    /// <summary>The typed boundary a capability finding resolved to, as a <c>CoachLimitationCode</c> ordinal.</summary>
    public int? GroundingLimitationCode { get; set; }

    /// <summary>The shadow router's label, as a <c>CoachShadowRouteLabel</c> ordinal. Telemetry only.</summary>
    public int? GroundingShadowLabel { get; set; }
}
