using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// One content-free record that a learner reached for something the coach could not do.
/// </summary>
/// <remarks>
/// <para>
/// Every column on this entity is deliberately a bounded identifier, an enum ordinal, a
/// timestamp, a count, or a closed-vocabulary code. <b>There is no payload column, protected or
/// otherwise, and no free-text column</b> — exactly like <c>CoachWriteAudit</c>, and for the same
/// reason: a reviewer can read this declaration and be certain no learner text, transcript,
/// vocabulary term, prompt, model completion, email, tool argument, or confirmation material can
/// reach it, without having to trust that a redaction routine was applied correctly at every call
/// site. <c>CoachOpportunityShapeTests</c> fails the build if a payload-shaped or text-shaped
/// column is added.
/// </para>
/// <para>
/// The specifics of what a learner asked for survive only inside the two encrypted
/// <c>CoachMessage</c> rows that <see cref="EvidenceMessageId"/> and
/// <see cref="EvidenceOfferMessageId"/> point at. Reading them is a separate, explicit,
/// self-auditing operator action.
/// </para>
/// <para>
/// <b>No foreign key</b>, deliberately, and for the same reason <c>CoachWriteAudit</c> has none:
/// the ledger must still describe a refusal for a conversation or an operation that no longer
/// exists. Evidence resolution therefore fails closed — a pointer whose message no longer
/// resolves renders as "unavailable", never as an error.
/// </para>
/// <para>
/// PostgreSQL-only, on <c>CoachDbContext</c>. Coach state never syncs to a device, so this never
/// joins the CoreSync entity set and never produces a mobile SQLite migration.
/// </para>
/// </remarks>
public sealed class CoachOpportunity
{
    /// <summary>Server-assigned identifier for this row.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The owning learner. The only ownership authority — every query filters on this, or is a
    /// content-free aggregate that returns counts and never an identifier.
    /// </summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Reserved for a future tenant boundary. Never queried, never keyed.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The conversation this happened in.
    /// <b>Forced null when <see cref="Disposition"/> is
    /// <see cref="CoachOpportunityDisposition.AggregateOnly"/>.</b>
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>The turn identity, when one was known. Null on every aggregate-only row.</summary>
    public string? TurnId { get; set; }

    /// <summary>The durable turn operation, when one was known. Null on every aggregate-only row.</summary>
    public string? TurnOperationId { get; set; }

    /// <summary>What kind of gap this is. Stored as an ordinal.</summary>
    public CoachOpportunityKind Kind { get; set; }

    /// <summary>Whether this row is individually reviewable or only counted. Stored as an ordinal.</summary>
    public CoachOpportunityDisposition Disposition { get; set; }

    /// <summary>Which boundary observed it. Stored as an ordinal.</summary>
    public CoachOpportunitySurface Surface { get; set; }

    /// <summary>
    /// What the learner was reaching for. Always a member of
    /// <see cref="CoachOpportunityCapabilityCodes.All"/>; the recorder drops anything else.
    /// </summary>
    public string CapabilityCode { get; set; } = string.Empty;

    /// <summary>
    /// The registered tool name, when a tool was involved.
    /// </summary>
    /// <remarks>
    /// Validated against <c>ICoachToolRegistry.IsRegistered</c> — deliberately the registry and
    /// not <c>CoachToolNames.All</c>, which is an alias for the core five and would silently
    /// reject every Sam read and write tool.
    /// </remarks>
    public string? ToolName { get; set; }

    /// <summary>The registered risk class, when a tool was involved. Stored as an ordinal.</summary>
    public CoachToolRiskClass? RiskClass { get; set; }

    /// <summary>
    /// Why the server said no, from an existing closed vocabulary. Never a message, never an
    /// exception string, never anything derived from learner input.
    /// </summary>
    public string? FailureCode { get; set; }

    /// <summary>The turn's stop reason, when the surface was the turn outcome. Stored as an ordinal.</summary>
    public CoachStopReason? StopReason { get; set; }

    /// <summary>What the learner's message was answering, when anything. Stored as an ordinal.</summary>
    public CoachOpportunityOfferLink OfferLink { get; set; }

    /// <summary>The learner's message for this turn. Null on every aggregate-only row.</summary>
    public string? EvidenceMessageId { get; set; }

    /// <summary>Its immutable position in the conversation.</summary>
    public long? EvidenceMessageSequence { get; set; }

    /// <summary>The prior coach message the answer was answering. Null on every aggregate-only row.</summary>
    public string? EvidenceOfferMessageId { get; set; }

    /// <summary>Its immutable position in the conversation.</summary>
    public long? EvidenceOfferMessageSequence { get; set; }

    /// <summary>The write ledger row, when one existed. Null on every aggregate-only row.</summary>
    public string? WriteOperationId { get; set; }

    /// <summary>An earlier row this one continues, when one was found.</summary>
    public string? RelatedOpportunityId { get; set; }

    /// <summary>
    /// The content-free identity of the underlying problem. Safe to log and safe to paste into a
    /// decision record. See <see cref="CoachOpportunityFingerprint"/>.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The UTC day this row buckets into.
    /// </summary>
    /// <remarks>
    /// One row per (learner, problem, UTC day). Bounds growth to at most one row per learner per
    /// problem per day, preserves a real frequency-over-time curve, and avoids a partial unique
    /// index that would behave differently on the relational test provider than on PostgreSQL.
    /// </remarks>
    public DateOnly DedupBucketDate { get; set; }

    /// <summary>How many times this problem happened for this learner on this day.</summary>
    public int OccurrenceCount { get; set; } = 1;

    /// <summary>When the first occurrence in this bucket was recorded.</summary>
    public DateTime FirstObservedAtUtc { get; set; }

    /// <summary>When the most recent occurrence in this bucket was recorded.</summary>
    public DateTime LastObservedAtUtc { get; set; }

    /// <summary>Where this row is in the review lifecycle. Stored as an ordinal.</summary>
    public CoachOpportunityStatus Status { get; set; } = CoachOpportunityStatus.New;

    /// <summary>When a reviewer last changed the status.</summary>
    public DateTime? ReviewedAtUtc { get; set; }

    /// <summary>
    /// The reviewer's decision, from a closed enum. Stored as an ordinal.
    /// </summary>
    /// <remarks>
    /// A closed enum rather than prose, deliberately: a free-text reviewer field is precisely
    /// where somebody would paste a learner's phrase to explain the entry, defeating every other
    /// control on this table.
    /// </remarks>
    public CoachOpportunityReviewerNoteCode? ReviewerNoteCode { get; set; }

    /// <summary>
    /// A repository-relative path to the spec or backlog entry that owns this now.
    /// </summary>
    /// <remarks>
    /// Server-validated against a fixed pattern, so this is a path reference and not a second
    /// free-text field.
    /// </remarks>
    public string? LinkedSpecPath { get; set; }

    /// <summary>
    /// How many times an operator revealed this row's encrypted evidence.
    /// </summary>
    /// <remarks>
    /// Self-auditing without a second audit table: the count and the timestamp live on the row
    /// being read, so a reveal cannot happen without leaving a mark on the thing that was read.
    /// </remarks>
    public int EvidenceRevealCount { get; set; }

    /// <summary>When evidence was last revealed.</summary>
    public DateTime? EvidenceLastRevealedAtUtc { get; set; }

    /// <summary>The row-shape contract version this row was written under.</summary>
    public int SchemaVersion { get; set; } = CoachOpportunityLimits.SchemaVersion;

    /// <summary>Optimistic concurrency token for the review and reveal paths.</summary>
    public int Version { get; set; }
}
