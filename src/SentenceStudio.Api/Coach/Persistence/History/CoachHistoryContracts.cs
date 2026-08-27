using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>Why a history operation did not succeed.</summary>
/// <remarks>
/// Every store returns a status rather than throwing for expected outcomes. An unknown
/// conversation, a stale cursor, and a hostile owner are all normal traffic on a public API;
/// turning them into exceptions makes the caller's happy path depend on catch blocks.
/// </remarks>
public enum CoachHistoryStatus
{
    /// <summary>The operation succeeded.</summary>
    Success = 0,

    /// <summary>No trusted owner was supplied, so no data was read or written.</summary>
    NoOwner,

    /// <summary>The conversation does not exist for this owner, or is deleted.</summary>
    NotFound,

    /// <summary>The supplied cursor was tampered with, expired, or belongs to another owner.</summary>
    InvalidCursor,

    /// <summary>The request violated a size or shape bound.</summary>
    InvalidRequest,

    /// <summary>A concurrent write won. The caller should re-read and retry.</summary>
    Conflict,

    /// <summary>
    /// The write presented a fencing token another worker has already superseded. Nothing was
    /// written, and the caller must discard its work rather than retry it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Conflict"/> on purpose: a conflict says "someone else went first,
    /// try again", and this says "you are not the writer any more, stop". Retrying a lost lease
    /// is exactly the duplicate the fencing token exists to prevent.
    /// </remarks>
    LeaseLost
}

/// <summary>
/// The token a superseded worker cannot forge: which operation is writing, which worker holds
/// its lease, and the fencing version that lease was granted at.
/// </summary>
/// <remarks>
/// <para>
/// Presented with every durable side effect a turn produces, so the write and the ownership
/// check happen together. A check performed before the write and trusted afterwards is a
/// time-of-check/time-of-use gap: a takeover that commits in between leaves two workers both
/// convinced they own the conversation, and both appending to the same transcript.
/// </para>
/// <para>
/// The three fields are exactly what <see cref="CoachTurnOperation"/> stores, so verifying one is
/// a single predicate on a single row rather than a join or a second read.
/// </para>
/// </remarks>
/// <param name="OperationId">The operation the write belongs to.</param>
/// <param name="LeaseOwner">The worker identity the lease was granted to.</param>
/// <param name="FencingVersion">The fencing version that grant carried.</param>
public sealed record CoachTurnFence(string OperationId, string LeaseOwner, long FencingVersion);

/// <summary>A conversation as durable history sees it, with the title decrypted.</summary>
/// <param name="Id">The opaque conversation identifier.</param>
/// <param name="Title">The decrypted title, or null when the stored title is unreadable.</param>
/// <param name="TitleSource">Whether the server or the learner set the title.</param>
/// <param name="TargetLanguageCode">The non-sensitive BCP-47 code, when scoped.</param>
/// <param name="Status">Active or hidden-pending-purge.</param>
/// <param name="HistoryStartsAt">When durable visible history begins.</param>
/// <param name="LastSequence">The highest allocated message sequence.</param>
/// <param name="Version">The row concurrency token, for optimistic writes.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last change time (UTC).</param>
public sealed record CoachConversationRecord(
    string Id,
    string? Title,
    CoachConversationTitleSource TitleSource,
    string? TargetLanguageCode,
    CoachConversationStatus Status,
    DateTime HistoryStartsAt,
    long LastSequence,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>False when the stored title could not be decrypted, so the UI can say so.</summary>
    public bool IsTitleReadable => Title is not null;
}

/// <summary>One page of conversations.</summary>
/// <param name="Status">The outcome. Items are empty unless this is success.</param>
/// <param name="Items">The page, ordered newest-updated first.</param>
/// <param name="NextCursor">An opaque cursor for the next page, or null at the end.</param>
public sealed record CoachConversationPage(
    CoachHistoryStatus Status,
    IReadOnlyList<CoachConversationRecord> Items,
    string? NextCursor)
{
    /// <summary>The empty page for a failed or owner-less read.</summary>
    public static CoachConversationPage Empty(CoachHistoryStatus status) =>
        new(status, Array.Empty<CoachConversationRecord>(), null);
}

/// <summary>The result of a single-conversation operation.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Conversation">The conversation, when the outcome is success.</param>
public sealed record CoachConversationResult(CoachHistoryStatus Status, CoachConversationRecord? Conversation)
{
    /// <summary>Builds a failure result.</summary>
    public static CoachConversationResult Failed(CoachHistoryStatus status) => new(status, null);
}

/// <summary>What a new conversation needs.</summary>
/// <param name="Title">The initial title. Bounded and encrypted before storage.</param>
/// <param name="TitleSource">Whether the server or the learner supplied it.</param>
/// <param name="TargetLanguageCode">The non-sensitive BCP-47 code, when scoped.</param>
/// <param name="ConversationId">An explicit id, for a caller that must control identity.</param>
public sealed record CreateCoachConversationRequest(
    string Title,
    CoachConversationTitleSource TitleSource = CoachConversationTitleSource.Generated,
    string? TargetLanguageCode = null,
    string? ConversationId = null);

/// <summary>One message as durable history sees it, with the payload decrypted.</summary>
/// <param name="Id">The opaque message identifier.</param>
/// <param name="ConversationId">The owning conversation.</param>
/// <param name="Sequence">The immutable position in the conversation.</param>
/// <param name="Role">Who produced the message.</param>
/// <param name="Kind">How it renders.</param>
/// <param name="Payload">The decrypted payload, or null when unreadable.</param>
/// <param name="SchemaVersion">The payload contract version the row was written under.</param>
/// <param name="OperationId">The turn operation that produced it, when any.</param>
/// <param name="CreatedAt">The canonical server timestamp (UTC).</param>
public sealed record CoachMessageRecord(
    string Id,
    string ConversationId,
    long Sequence,
    CoachMessageRole Role,
    CoachMessageKind Kind,
    CoachMessagePayload? Payload,
    int SchemaVersion,
    string? OperationId,
    DateTime CreatedAt)
{
    /// <summary>
    /// False when the payload could not be decrypted. The row is still returned so the ledger
    /// keeps its shape and the client can render a recoverable placeholder rather than silently
    /// losing a turn.
    /// </summary>
    public bool IsReadable => Payload is not null;
}

/// <summary>One page of messages, always in chronological order.</summary>
/// <param name="Status">The outcome. Items are empty unless this is success.</param>
/// <param name="Items">The page, oldest first.</param>
/// <param name="PreviousCursor">An opaque cursor for the page before this one, or null at the start.</param>
/// <param name="UnreadableCount">How many rows in the page failed to decrypt.</param>
public sealed record CoachMessagePage(
    CoachHistoryStatus Status,
    IReadOnlyList<CoachMessageRecord> Items,
    string? PreviousCursor,
    int UnreadableCount)
{
    /// <summary>The empty page for a failed or owner-less read.</summary>
    public static CoachMessagePage Empty(CoachHistoryStatus status) =>
        new(status, Array.Empty<CoachMessageRecord>(), null, 0);
}

/// <summary>What a message append needs.</summary>
/// <param name="ConversationId">The target conversation.</param>
/// <param name="Role">Who produced the message.</param>
/// <param name="Kind">How it renders.</param>
/// <param name="Payload">The visible payload. Bounded and encrypted before storage.</param>
/// <param name="OperationId">The turn operation that produced it, when any.</param>
/// <param name="MessageId">An explicit id, for a caller that must control identity.</param>
/// <param name="Fence">
/// The caller's fencing token. When present, the append is admitted only while the named
/// operation is still non-terminal and still held at that fencing version by that lease owner,
/// checked in the same transaction as the insert. Null for writes that belong to no turn.
/// </param>
public sealed record AppendCoachMessageRequest(
    string ConversationId,
    CoachMessageRole Role,
    CoachMessageKind Kind,
    CoachMessagePayload Payload,
    string? OperationId = null,
    string? MessageId = null,
    CoachTurnFence? Fence = null);

/// <summary>The result of appending one message.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Message">The appended message, when the outcome is success.</param>
public sealed record CoachMessageAppendResult(CoachHistoryStatus Status, CoachMessageRecord? Message)
{
    /// <summary>Builds a failure result.</summary>
    public static CoachMessageAppendResult Failed(CoachHistoryStatus status) => new(status, null);
}

/// <summary>What a claim attempt produced.</summary>
public enum CoachTurnClaimOutcome
{
    /// <summary>No trusted owner was supplied.</summary>
    NoOwner = 0,

    /// <summary>The caller owns the operation and may execute the turn.</summary>
    Claimed,

    /// <summary>The same key and the same request already finished. Replay the stored outcome.</summary>
    ReplayCompleted,

    /// <summary>The same key and the same request is already running elsewhere.</summary>
    InProgress,

    /// <summary>The same key arrived with a different request. The caller must not proceed.</summary>
    PayloadConflict,

    /// <summary>Another operation holds the single-writer slot for this conversation.</summary>
    ConversationBusy,

    /// <summary>The conversation does not exist for this owner, or is deleted.</summary>
    ConversationNotFound,

    /// <summary>The same key already ended in a terminal non-success state.</summary>
    ReplayTerminal
}

/// <summary>A turn operation as durable history sees it. Protected fields stay protected.</summary>
/// <param name="Id">The operation identifier.</param>
/// <param name="ConversationId">The conversation it writes to.</param>
/// <param name="Status">The durable state.</param>
/// <param name="LeaseOwner">The worker that holds the lease, when any.</param>
/// <param name="LeaseExpiresAt">When the lease stops being valid (UTC).</param>
/// <param name="FencingVersion">The monotonic fencing counter.</param>
/// <param name="AttemptCount">How many workers have claimed it.</param>
/// <param name="CancelRequested">Whether cancellation was durably requested.</param>
/// <param name="BaseConversationVersion">The conversation version it was accepted against.</param>
/// <param name="LearnerMessageSequence">The learner message this turn responds to.</param>
/// <param name="FirstResponseSequence">The first appended response sequence.</param>
/// <param name="LastResponseSequence">The last appended response sequence.</param>
/// <param name="ErrorCode">A content-free failure code, when failed.</param>
/// <param name="CreatedAt">Acceptance time (UTC).</param>
/// <param name="UpdatedAt">Last change time (UTC).</param>
public sealed record CoachTurnOperationRecord(
    string Id,
    string ConversationId,
    CoachTurnOperationStatus Status,
    string? LeaseOwner,
    DateTime? LeaseExpiresAt,
    long FencingVersion,
    int AttemptCount,
    bool CancelRequested,
    int BaseConversationVersion,
    long? LearnerMessageSequence,
    long? FirstResponseSequence,
    long? LastResponseSequence,
    string? ErrorCode,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>The result of a claim attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Operation">The operation row, when one was found or created.</param>
/// <param name="FencingVersion">The token the caller must present to finalize.</param>
/// <param name="StoredOutcome">The decrypted durable outcome, on a completed replay.</param>
/// <param name="StoredOutcomeSchemaVersion">The outcome contract version, on a completed replay.</param>
public sealed record CoachTurnClaimResult(
    CoachTurnClaimOutcome Outcome,
    CoachTurnOperationRecord? Operation,
    long FencingVersion,
    string? StoredOutcome,
    int? StoredOutcomeSchemaVersion)
{
    /// <summary>Builds a result with no operation row.</summary>
    public static CoachTurnClaimResult Failed(CoachTurnClaimOutcome outcome) =>
        new(outcome, null, 0, null, null);
}

/// <summary>What a claim needs.</summary>
/// <param name="ConversationId">The conversation to write to.</param>
/// <param name="IdempotencyKey">The client's retry key. Digested, never stored in the clear.</param>
/// <param name="RequestPayload">
/// The canonical request bytes. Digested and encrypted so a same-key/different-payload retry is
/// detectable without storing plaintext or a brute-forceable bare hash.
/// </param>
/// <param name="LeaseOwner">The worker identity taking the lease.</param>
/// <param name="LeaseDuration">How long the lease is valid before another worker may take over.</param>
/// <param name="OperationId">An explicit id, for a caller that must control identity.</param>
public sealed record ClaimCoachTurnRequest(
    string ConversationId,
    string IdempotencyKey,
    string RequestPayload,
    string LeaseOwner,
    TimeSpan LeaseDuration,
    string? OperationId = null);

/// <summary>Why a finalization attempt did not take effect.</summary>
public enum CoachTurnFinalizeOutcome
{
    /// <summary>No trusted owner was supplied.</summary>
    NoOwner = 0,

    /// <summary>The state change was written.</summary>
    Success,

    /// <summary>The operation does not exist for this owner.</summary>
    NotFound,

    /// <summary>
    /// The caller's lease was taken over or expired. The caller must discard its work rather
    /// than write output another worker has already produced.
    /// </summary>
    LeaseLost,

    /// <summary>The operation had already reached a terminal state.</summary>
    AlreadyTerminal,

    /// <summary>A concurrent write won. Re-read and retry.</summary>
    Conflict
}

/// <summary>The result of finalizing, renewing, or cancelling a turn.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Operation">The operation row after the attempt, when found.</param>
public sealed record CoachTurnFinalizeResult(CoachTurnFinalizeOutcome Outcome, CoachTurnOperationRecord? Operation)
{
    /// <summary>Builds a result with no operation row.</summary>
    public static CoachTurnFinalizeResult Failed(CoachTurnFinalizeOutcome outcome) => new(outcome, null);
}

/// <summary>A completed operation's durable outcome, decrypted.</summary>
/// <param name="Payload">The stored outcome, or null when it could not be decrypted.</param>
/// <param name="SchemaVersion">The outcome contract version the row was written under.</param>
/// <param name="FirstResponseSequence">The first ledger sequence the turn appended.</param>
/// <param name="LastResponseSequence">The last ledger sequence the turn appended.</param>
public sealed record CoachTurnOutcome(
    string? Payload,
    int? SchemaVersion,
    long? FirstResponseSequence,
    long? LastResponseSequence)
{
    /// <summary>False when the stored outcome could not be decrypted.</summary>
    public bool IsReadable => Payload is not null;
}

/// <summary>One conversation and its messages, streamed for export.</summary>
/// <param name="Conversation">The conversation metadata.</param>
/// <param name="Messages">The messages, streamed in chronological order.</param>
public sealed record CoachConversationExport(
    CoachConversationRecord Conversation,
    IAsyncEnumerable<CoachMessageRecord> Messages);

/// <summary>
/// One tool call, reduced to facts a stored turn may keep.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member is a closed code, a count, a duration, or a bounded server identifier.</b> That
/// is checkable from this declaration alone, which is the point: a reviewer does not have to trace
/// what fills it to know what it can hold. <c>CoachTurnTraceShapeTests</c> reflects over the record
/// and fails on a <c>string</c> or <c>object</c> member that is not the registered tool name.
/// </para>
/// <para>
/// <b>The scope object never gets here.</b> <c>CoachResultScope</c> rides the in-memory observation
/// so W3's evidence and this trace can each project from one capture; what crosses the persistence
/// boundary is the handful of closed codes below. Serializing the scope itself would put its six
/// foundation members and its whole future shape into a protected column nobody versioned.
/// </para>
/// <para>
/// <b>No argument values.</b> <see cref="ArgumentMask"/> records which arguments were present and
/// never what they were, so a trace cannot become a transcript of what the learner searched for.
/// </para>
/// </remarks>
/// <param name="Ordinal">Position within the turn, 1-based.</param>
/// <param name="ToolName">The registered tool name. A build-time constant, never a model string.</param>
/// <param name="Outcome">How the call ended.</param>
/// <param name="FailureKind">The typed refusal kind, when the call was refused.</param>
/// <param name="ArgumentMask">Which arguments were present. Presence only.</param>
/// <param name="ElapsedMs">Wall time for the call, in whole milliseconds.</param>
/// <param name="Coverage">How much of the learner's data the read covered.</param>
/// <param name="DefinitionCode">Which definition of the population the read used.</param>
/// <param name="WithheldReason">Why rows were withheld, when any were.</param>
/// <param name="MatchedCount">How many rows matched, when the read counted a population.</param>
/// <param name="ReturnedCount">How many rows the answer carried.</param>
/// <param name="WithheldCount">How many matching rows were deliberately not returned.</param>
/// <param name="Truncated">True when paging dropped rows that had cleared every filter.</param>
public sealed record CoachTurnTraceEntry(
    int Ordinal,
    string ToolName,
    SentenceStudio.Api.Coach.Tools.Observation.CoachToolCallOutcome Outcome,
    SentenceStudio.Api.Coach.Tools.CoachToolFailureKind? FailureKind,
    SentenceStudio.Api.Coach.Tools.Observation.CoachToolArgumentMask ArgumentMask,
    int ElapsedMs,
    SentenceStudio.Api.Coach.Tools.CoachScopeCoverage Coverage,
    SentenceStudio.Api.Coach.Tools.CoachScopeDefinition DefinitionCode,
    SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason WithheldReason,
    int? MatchedCount,
    int? ReturnedCount,
    int? WithheldCount,
    bool Truncated)
{
    private readonly string _toolName =
        SentenceStudio.Api.Coach.Tools.Observation.CoachTurnTraceToolName.Normalize(ToolName);

    /// <summary>
    /// The registered tool name, or <c>CoachToolNames.Unregistered</c> when the frozen registry
    /// does not contain the supplied value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Normalized on the member, not only at the projection.</b> The projection is the intended
    /// road in, but it is not the only one: this record is public, positional, and reachable by
    /// <c>with</c> and by the deserializer. A boundary that only the intended caller honours is a
    /// convention, and the whole reason the trace's one string exception is defensible is that it
    /// is not a convention. Every construction path — primary constructor, <c>with</c> expression,
    /// JSON — goes through this accessor.
    /// </para>
    /// <para>
    /// Deliberately not a throw. The entry and its ordinal are kept, so a turn's record keeps its
    /// length and its numbering; only the unrecognised name is replaced. A raw value never reaches
    /// the backing field, so it cannot reach the serialized trace even in the same process.
    /// </para>
    /// </remarks>
    public string ToolName
    {
        get => _toolName;
        init => _toolName =
            SentenceStudio.Api.Coach.Tools.Observation.CoachTurnTraceToolName.Normalize(value);
    }
}

/// <summary>
/// What a turn's tools did, as a content-free summary.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BudgetUsed"/> and <see cref="BudgetLimit"/> are recorded <b>once, here, at the turn
/// boundary</b> — never as a synthetic entry in <see cref="Calls"/>. A budget refusal is raised by
/// the outer budget wrapper before the observation seam runs, so no tool call corresponds to it;
/// inventing one would report a limit as a tool that failed, which is the specific
/// double-counting the seam's nesting was arranged to avoid.
/// </para>
/// <para>
/// Both are nullable because the budget object lives inside the agent arms, which this batch does
/// not own. The shape and the read path are complete; the values arrive when their owner supplies
/// them through <c>ICoachTurnObservationBuffer.RecordBudget</c>.
/// </para>
/// </remarks>
/// <param name="Calls">The turn's tool calls, ordinal-ascending.</param>
/// <param name="BudgetUsed">Tool calls counted against the turn's budget, when known.</param>
/// <param name="BudgetLimit">The turn's tool-call cap, when known.</param>
public sealed record CoachTurnTraceSummary(
    IReadOnlyList<CoachTurnTraceEntry> Calls,
    int? BudgetUsed,
    int? BudgetLimit);

/// <summary>
/// The stored shape of a completed turn's outcome, at schema version 2.
/// </summary>
/// <remarks>
/// <para>
/// Version 1 stored the answer at the root. Version 2 wraps it so a nullable trace can sit beside
/// it, and the reader branches on the stored version rather than sniffing the JSON — a v1 row still
/// yields its answer, with <see cref="Trace"/> null. That is the whole compatibility contract, and
/// it is why the version was bumped rather than the answer quietly gaining a sibling property.
/// </para>
/// <para>
/// A row written under a version this build does not know is treated as absent, exactly as before.
/// </para>
/// </remarks>
/// <param name="Answer">The turn's answer, as version 1 stored it at the root.</param>
/// <param name="Trace">The content-free tool trace, or null for a version-1 row.</param>
/// <param name="Dispute">
/// The open correction, or null when the learner has not disputed this turn. Null for versions 1
/// and 2.
/// </param>
/// <param name="Grounding">
/// What the honesty layer did to this turn, or null when it did nothing durable.
/// </param>
/// <remarks>
/// <b>Grounding arrives without a version bump, and that is the ruling rather than an oversight.</b>
/// A named section is invisible to a reader that does not look for it: the parser reads sections by
/// name and <c>TryGetSection</c> answers null for one that is absent, so a build predating this
/// property reads a payload containing it and returns exactly what it returned before. Bumping to
/// version 4 would have been strictly worse — during a rolling deployment an older replica reading
/// a v4 row falls into the unknown-version arm and reports <em>no answer at all</em>, which is the
/// failure the version-2 bump comment warns about, arriving through the mechanism meant to prevent
/// it. A frozen pre-W9 reader emulation in the tests holds this claim.
/// </remarks>
public sealed record CoachStoredTurnOutcome(
    SentenceStudio.Contracts.Coach.CoachTurnResponse? Answer,
    CoachTurnTraceSummary? Trace,
    CoachTurnDisputeState? Dispute = null,

    // Omitted rather than written as an explicit null. Until R2 populates it, every row this build
    // writes is then BYTE-IDENTICAL to one a pre-W9 build wrote — the rollout introduces no payload
    // change at all, and "absent" means the same thing to both readers instead of meaning "present
    // and null" to one of them.
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SentenceStudio.Api.Coach.Validation.Claims.CoachGroundingTurnSummary? Grounding = null);

/// <summary>
/// A learner's open correction of a coach claim, held beside the turn it disputes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content-free, and this is the whole design.</b> The learner's correction text is already in
/// the encrypted message ledger, where it belongs and where account erasure already reaches it.
/// Copying it here would put learner prose into the protected outcome — a second copy, with a
/// second retention story and a second erasure path, for no benefit the closed signal does not
/// already give.
/// </para>
/// <para>
/// So the dispute carries a signal code, a bounded identifier for the message it disputes, and
/// timestamps. Everything a rule needs to judge the next answer, and nothing a reader could
/// reconstruct the conversation from.
/// </para>
/// <para>
/// <b>Keyed to the disputed message, not to the session.</b> A learner correcting one claim has not
/// disputed everything the coach ever said, and a session-scoped flag would make the next answer
/// defend a claim nobody challenged.
/// </para>
/// </remarks>
/// <param name="Signal">Which kind of correction the learner made.</param>
/// <param name="DisputedMessageId">
/// The ledger identifier of the coach message under dispute. An identifier, never the text: the
/// message itself stays in the encrypted ledger.
/// </param>
/// <param name="OpenedAtUtc">When the dispute opened, whole-second UTC.</param>
/// <param name="ResolvedAtUtc">When it closed, or null while it is open.</param>
/// <param name="Resolution">How it closed, or <see cref="CoachDisputeResolution.Open"/>.</param>
/// <param name="DisputedDefinitionCodes">
/// The read definitions the disputed answer was built from, so the next answer can be checked for
/// having used materially different parameters. Closed codes, never arguments.
/// </param>
public sealed record CoachTurnDisputeState(
    SentenceStudio.Api.Coach.Application.CoachCorrectionSignal Signal,
    string DisputedMessageId,
    DateTime OpenedAtUtc,
    DateTime? ResolvedAtUtc,
    CoachDisputeResolution Resolution,
    IReadOnlyList<SentenceStudio.Api.Coach.Tools.CoachScopeDefinition> DisputedDefinitionCodes)
{
    /// <summary>True while the dispute still constrains the next answer.</summary>
    public bool IsOpen => Resolution == CoachDisputeResolution.Open;

    /// <summary>
    /// The longest a bounded message identifier may be.
    /// </summary>
    /// <remarks>
    /// A ledger identifier is a GUID-shaped string. The bound exists so that a caller cannot use
    /// this field as a smuggling channel for a query, a term, or an answer — a length cap turns
    /// "do not put prose here" from a comment into something the validator enforces.
    /// </remarks>
    public const int MaxDisputedMessageIdLength = 64;
}

/// <summary>
/// How a dispute ended, or that it has not.
/// </summary>
/// <remarks>
/// The three closing routes are the three the plan permits the next answer to take: re-read with
/// materially different parameters and say what changed, correct the prior claim by name, or state
/// an honest limitation. Nothing else closes a dispute — in particular, answering again more
/// confidently does not.
/// </remarks>
/// <remarks>
/// String-serialized for the same reason the correction signal is: this is persisted state, and an
/// ordinal would let an inserted member turn every stored open dispute into a resolved one.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CoachDisputeResolution
{
    /// <summary>Still open. The next answer is judged against it.</summary>
    Open = 0,

    /// <summary>The coach re-read with materially different parameters.</summary>
    ResolvedByReRead = 1,

    /// <summary>The coach named and corrected its prior claim.</summary>
    ResolvedByCorrection = 2,

    /// <summary>The coach stated an honest limitation instead.</summary>
    ResolvedByLimitation = 3,

    /// <summary>The learner dismissed it.</summary>
    DismissedByLearner = 4
}
