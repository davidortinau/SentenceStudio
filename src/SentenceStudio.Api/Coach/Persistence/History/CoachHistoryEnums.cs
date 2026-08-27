namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// The lifecycle of one durable conversation.
/// </summary>
/// <remarks>
/// Stored as an ordinal. Members may only be appended — inserting one silently re-labels
/// every row already written. <c>CoachHistoryStoredEnumContractTests</c> pins the values.
/// </remarks>
public enum CoachConversationStatus
{
    /// <summary>Visible, listable, and resumable.</summary>
    Active = 0,

    /// <summary>
    /// Deletion was confirmed. The row is hidden from every read path immediately and the
    /// purge removes it and its children afterwards.
    /// </summary>
    Deleting = 1,

    /// <summary>
    /// The learner ended the conversation. It stays readable, listable, and exportable; it only
    /// refuses new turns.
    /// </summary>
    /// <remarks>
    /// This is durable intent, unlike an expired checkpoint. Without it, closing a conversation
    /// would last only as long as the 24-hour checkpoint and the next turn would silently
    /// reopen it, which is not what "close" means to the person who tapped it.
    /// </remarks>
    Closed = 2
}

/// <summary>Where a conversation title came from.</summary>
public enum CoachConversationTitleSource
{
    /// <summary>The server generated the default title. Safe to replace automatically.</summary>
    Generated = 0,

    /// <summary>The learner renamed the conversation. Never overwritten by the server.</summary>
    Learner = 1
}

/// <summary>
/// The durable state of one turn operation.
/// </summary>
/// <remarks>
/// The closed set approved for the first history slice. Stored as an ordinal; append only.
/// </remarks>
public enum CoachTurnOperationStatus
{
    /// <summary>Accepted and durable, but no worker has started it.</summary>
    Pending = 0,

    /// <summary>A worker holds a valid lease and is executing the turn.</summary>
    Running = 1,

    /// <summary>Finished with a durable outcome that a replay returns verbatim.</summary>
    Completed = 2,

    /// <summary>Finished without a usable outcome. Carries a content-free error code.</summary>
    Failed = 3,

    /// <summary>Stopped because cancellation was requested before finalization.</summary>
    Cancelled = 4
}

/// <summary>
/// What a protected payload is, so ciphertext written for one purpose can never be
/// unprotected as another.
/// </summary>
/// <remarks>
/// The member name — not its ordinal — is part of the protection purpose, so renaming a
/// member invalidates existing ciphertext. Add, never rename.
/// </remarks>
public enum CoachProtectedContentKind
{
    /// <summary>A conversation title.</summary>
    ConversationTitle = 0,

    /// <summary>A canonical visible-message payload.</summary>
    MessagePayload = 1,

    /// <summary>The digest of a turn request, used for same-key/different-payload detection.</summary>
    TurnRequestDigest = 2,

    /// <summary>The durable outcome replayed for a completed turn.</summary>
    TurnOutcome = 3,

    /// <summary>An opaque, owner-bound pagination cursor handed to a client.</summary>
    ListCursor = 4,

    /// <summary>The typed value of one learner memory fact.</summary>
    /// <remarks>
    /// Appended, never renumbered: the member name is part of the protection purpose, so renaming
    /// or reordering would make every stored memory value undecryptable.
    /// </remarks>
    MemoryFactValue = 5,

    /// <summary>The canonical arguments of a proposed learner-owned write.</summary>
    WriteOperationArguments = 6,

    /// <summary>The snapshot of the fields a reversible write is about to change.</summary>
    WriteOperationPriorState = 7,

    /// <summary>The learner-visible preview shown before a write is accepted.</summary>
    WriteOperationPreview = 8,

    /// <summary>The durable receipt replayed for an executed write.</summary>
    WriteOperationReceipt = 9
}
