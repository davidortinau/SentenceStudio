using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// One thing Sam remembers about one learner, encrypted at rest.
/// </summary>
/// <remarks>
/// <para>
/// v1 is deliberately one table. There is no separate event table and no provenance table because
/// every fact has exactly one source: an explicit learner statement. Adding a second table now
/// would model a history nobody is allowed to write.
/// </para>
/// <para>
/// The learner's words are never stored. <see cref="SourceConversationId"/> and
/// <see cref="SourceMessageId"/> are opaque metadata, and the evidence fields are counts and dates
/// only. The value itself lives encrypted in <see cref="ProtectedValue"/>, bound to the owner, the
/// row id, and the protection version, so a row lifted into another owner's account is unreadable
/// rather than merely mis-attributed.
/// </para>
/// </remarks>
public class CoachMemoryFact
{
    /// <summary>Stable identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The owning learner. The only authority in v1.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>
    /// A tenant hint carried for future routing. Never queried, never keyed, never part of the
    /// protection purpose.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>Which closed kind this fact is.</summary>
    public CoachMemoryKind Kind { get; set; }

    /// <summary>Whether the fact is language-scoped or explicitly global.</summary>
    public CoachMemoryScope Scope { get; set; }

    /// <summary>The scoped language, or null when the scope is global.</summary>
    public string? TargetLanguageCode { get; set; }

    /// <summary>
    /// The derived, non-null scope key (<c>global</c> or <c>lang:{tag}</c>).
    /// </summary>
    /// <remarks>
    /// A nullable language column cannot carry the uniqueness rule: PostgreSQL treats NULLs as
    /// distinct, so two global facts of the same kind would both be allowed. The derived key makes
    /// "one active fact per owner, kind, and scope" expressible as a real database constraint.
    /// </remarks>
    public string ScopeKey { get; set; } = CoachMemorySchema.GlobalScopeKey;

    /// <summary>The encrypted typed value.</summary>
    public string ProtectedValue { get; set; } = string.Empty;

    /// <summary>The JSON shape inside <see cref="ProtectedValue"/>.</summary>
    public int ValueVersion { get; set; } = CoachMemorySchema.ValueVersion;

    /// <summary>The content-protection version used to write <see cref="ProtectedValue"/>.</summary>
    public int ProtectionVersion { get; set; }

    /// <summary>Where the fact sits in its lifecycle.</summary>
    public CoachMemoryStatus Status { get; set; }

    /// <summary>How the fact came to exist.</summary>
    public CoachMemoryProvenance Provenance { get; set; }

    /// <summary>The conversation the learner stated it in. Opaque metadata; no foreign key.</summary>
    public string? SourceConversationId { get; set; }

    /// <summary>The message the learner stated it in. Opaque metadata; no foreign key.</summary>
    public string? SourceMessageId { get; set; }

    /// <summary>How many explicit statements support the fact. Bounded count, never text.</summary>
    public int EvidenceCount { get; set; }

    /// <summary>When the first supporting statement was made.</summary>
    public DateTime EvidenceFirstObservedAt { get; set; }

    /// <summary>When the most recent supporting statement was made.</summary>
    public DateTime EvidenceLastObservedAt { get; set; }

    /// <summary>When the candidate row was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the row last changed.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>When the learner approved it.</summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>When it was last selected into a prompt.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>When it stops being eligible, or null when it does not expire.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>The fact this one replaced on approval.</summary>
    public string? SupersedesId { get; set; }

    /// <summary>
    /// Optimistic concurrency. A plain integer rather than a provider row version, so the tests and
    /// production see the same conflict behaviour.
    /// </summary>
    public int Version { get; set; }
}
