namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// The aggregate root for one durable, learner-visible Sam conversation.
/// </summary>
/// <remarks>
/// <para>
/// This row carries no learner text in plaintext. The title — the only free text a learner can
/// put on the aggregate — lives encrypted in <see cref="ProtectedTitle"/>. Everything else is
/// operational metadata: ordering timestamps, a sequence allocator, and version stamps.
/// </para>
/// <para>
/// This is deliberately <b>not</b> <c>CoachSession</c>. <c>CoachSession</c> remains the
/// replaceable 24-hour runtime checkpoint for the opaque agent state; this row is the durable
/// product record whose lifetime is controlled by the learner.
/// </para>
/// </remarks>
public sealed class CoachConversation
{
    /// <summary>Opaque application-owned identifier. EF never generates this value.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The owning learner. The only ownership authority; every query filters on it.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Forward-compatibility classification. Never queried, never keyed, never part of a
    /// protection purpose — see <see cref="CoachOwner"/>.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>The encrypted title. A plaintext scan of this column reveals nothing.</summary>
    public string ProtectedTitle { get; set; } = string.Empty;

    /// <summary>Whether the server generated the title or the learner renamed it.</summary>
    public CoachConversationTitleSource TitleSource { get; set; } = CoachConversationTitleSource.Generated;

    /// <summary>
    /// The non-sensitive BCP-47 code of the language being studied in this conversation.
    /// A language code is a coarse, non-identifying classification, so it stays plaintext for
    /// filtering. Null when the conversation is not scoped to one language.
    /// </summary>
    public string? TargetLanguageCode { get; set; }

    /// <summary>Active or hidden-pending-purge.</summary>
    public CoachConversationStatus Status { get; set; } = CoachConversationStatus.Active;

    /// <summary>
    /// The instant durable visible history begins for this conversation. Messages before it do
    /// not exist, so the UI can state truthfully that history starts here rather than implying
    /// a pre-cutover transcript was lost.
    /// </summary>
    public DateTime HistoryStartsAt { get; set; }

    /// <summary>
    /// The highest allocated message sequence. The allocator, guarded by
    /// <see cref="Version"/> and by the unique message index.
    /// </summary>
    public long LastSequence { get; set; }

    /// <summary>The metadata shape of this row, for future column migrations.</summary>
    public int MetadataSchemaVersion { get; set; } = CoachHistorySchema.ConversationMetadataVersion;

    /// <summary>The protector envelope version used for <see cref="ProtectedTitle"/>.</summary>
    public int ContentProtectionVersion { get; set; }

    /// <summary>Row concurrency token. Incremented by every write; also the fencing counter.</summary>
    public int Version { get; set; }

    /// <summary>When the conversation was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the row last changed (UTC). The list order key.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>When deletion was confirmed (UTC). Non-null means hidden from every read.</summary>
    public DateTime? DeletedAt { get; set; }
}
