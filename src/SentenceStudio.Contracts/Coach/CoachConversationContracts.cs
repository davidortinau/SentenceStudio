using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// Who named a conversation.
/// The zero value is Generated, so an unset value never claims the learner chose the title.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachConversationTitleOrigin.Unreadable), WireEnumFallbackKind.NeutralMember,
    "Unreadable already means \u201cthe stored title could not be read; render a placeholder\u201d, which "
    + "is the same situation. Generated \u2014 the zero value \u2014 is documented as \u201csafe to "
    + "replace silently\u201d, so collapsing onto it would let the client overwrite a title the learner "
    + "may have typed themselves.")]
public enum CoachConversationTitleOrigin
{
    /// <summary>The server built the title from a fallback rule. Safe to replace silently.</summary>
    Generated = 0,

    /// <summary>The learner typed the title. Never replaced by the server.</summary>
    Learner,

    /// <summary>The stored title could not be read. Render a placeholder, not an empty string.</summary>
    Unreadable
}

/// <summary>
/// One durable coach conversation, as the learner's client sees it.
/// </summary>
/// <remarks>
/// <para>
/// A conversation is the canonical, permanent thing. The 24-hour coaching session is a
/// replaceable checkpoint over it: the checkpoint expires, the conversation and its message
/// ledger do not. Nothing here identifies a checkpoint, a turn operation, a plan revision, or a
/// vocabulary item.
/// </para>
/// <para>
/// <see cref="StateVersion"/> is the concurrency token. Send it back on a rename or a close;
/// the server refuses the write when it has moved, which is what stops two devices from
/// silently overwriting each other's title.
/// </para>
/// </remarks>
public sealed class CoachConversationDto
{
    /// <summary>The conversation identifier. Opaque, owner-scoped, and stable for its life.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The display title. Never empty; unreadable titles come back as a placeholder.</summary>
    public required string Title { get; init; }

    /// <summary>Who chose <see cref="Title"/>.</summary>
    public required CoachConversationTitleOrigin TitleOrigin { get; init; }

    /// <summary>The BCP-47 code of the language being studied, when the conversation is scoped to one.</summary>
    public string? TargetLanguageCode { get; init; }

    /// <summary>The time the conversation was created.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The time the conversation last changed: a new message, a rename, or a close.</summary>
    public required DateTime UpdatedAtUtc { get; init; }

    /// <summary>
    /// The earliest point history is retained from. Messages before it were purged by retention
    /// and are gone; the client should show a boundary rather than pretend the thread starts here.
    /// </summary>
    public required DateTime HistoryStartsAtUtc { get; init; }

    /// <summary>How many messages the ledger holds, as the highest assigned position.</summary>
    public required long MessageCount { get; init; }

    /// <summary>The concurrency token. Send it back with a rename or a close.</summary>
    public required long StateVersion { get; init; }

    /// <summary>
    /// True when the conversation currently has a live coaching checkpoint accepting turns.
    /// </summary>
    /// <remarks>
    /// A false value never means the conversation is over: a new turn opens a fresh checkpoint
    /// rebuilt from the ledger. It exists so a client can tell "resume this thread" from
    /// "continue the run that is already open".
    /// </remarks>
    public required bool HasActiveCheckpoint { get; init; }

    /// <summary>
    /// True when the learner closed the conversation. It stays readable and exportable; it only
    /// refuses new turns until it is reopened.
    /// </summary>
    public required bool IsClosed { get; init; }
}

/// <summary>One page of conversations, newest first.</summary>
public sealed class CoachConversationPageDto
{
    /// <summary>The conversations on this page.</summary>
    public IReadOnlyList<CoachConversationDto> Items { get; init; } = Array.Empty<CoachConversationDto>();

    /// <summary>
    /// The cursor for the next, older page. Null when this is the last page.
    /// </summary>
    /// <remarks>
    /// Opaque and integrity-protected. A tampered or foreign cursor is rejected rather than
    /// interpreted, so a cursor can never be edited into a read of someone else's history.
    /// </remarks>
    public string? NextCursor { get; init; }
}

/// <summary>What a new conversation needs.</summary>
public sealed record StartCoachConversationRequest
{
    /// <summary>
    /// The client's retry key. Required: without it a dropped response creates a second empty
    /// conversation every time the client retries.
    /// </summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>
    /// The learner's title, when they typed one. Null asks the server for a dated fallback.
    /// </summary>
    /// <remarks>
    /// The server never asks a model to name a conversation. A generated title is a localized
    /// date, which cannot leak what was discussed into a list view or a notification.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>The BCP-47 code of the language being studied, when the client knows it.</summary>
    public string? TargetLanguageCode { get; init; }
}

/// <summary>A rename, a close, or both.</summary>
public sealed record UpdateCoachConversationRequest
{
    /// <summary>
    /// The <see cref="CoachConversationDto.StateVersion"/> the client last saw. The server
    /// refuses the write when the conversation has moved on.
    /// </summary>
    public long? ExpectedStateVersion { get; init; }

    /// <summary>The new title. Null leaves the title alone.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// True closes the conversation, false reopens it, null leaves it alone. A closed
    /// conversation keeps all its history and can be read and exported; it only refuses turns.
    /// </summary>
    public bool? Close { get; init; }
}

/// <summary>A receipt as durable history kept it.</summary>
/// <remarks>
/// Deliberately smaller than <see cref="CoachChangeReceiptDto"/>. The plan diff, the constraint
/// delta, and the vocabulary focus are live plan state, not conversation content: replaying a
/// months-old diff would describe a plan that no longer exists. History keeps what was said.
/// </remarks>
public sealed class CoachHistoryReceiptDto
{
    /// <summary>The receipt identifier.</summary>
    public required string ReceiptId { get; init; }

    /// <summary>The plan revision this receipt described.</summary>
    public required string RevisionId { get; init; }

    /// <summary>The localized summary shown at the time.</summary>
    public required string Summary { get; init; }

    /// <summary>The change lines shown at the time, in order.</summary>
    public IReadOnlyList<string> ChangeLines { get; init; } = Array.Empty<string>();
}

/// <summary>A suggestion as durable history kept it.</summary>
/// <remarks>
/// Holds what the learner was shown, not the machinery behind it: no delta, no preview, no
/// evidence, no vocabulary identifiers. A stored suggestion is a record of an offer, never a
/// re-appliable action.
/// </remarks>
public sealed class CoachHistorySuggestionDto
{
    /// <summary>The suggestion identifier as it was offered.</summary>
    public required string SuggestionId { get; init; }

    /// <summary>The localized reason shown at the time.</summary>
    public required string Rationale { get; init; }

    /// <summary>The change lines shown at the time, in order.</summary>
    public IReadOnlyList<string> ChangeLines { get; init; } = Array.Empty<string>();

    /// <summary>The accept label shown at the time.</summary>
    public string? AcceptLabel { get; init; }

    /// <summary>The reject label shown at the time.</summary>
    public string? RejectLabel { get; init; }
}

/// <summary>
/// One message from durable history: the public message, plus whatever structured content it
/// carried.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is the exact <see cref="CoachMessageDto"/> the turn returned, so a
/// client can bind history and a live turn through one path. The structured parts travel
/// alongside it rather than inside it, because the live turn contract carries them alongside too.
/// </remarks>
public sealed class CoachHistoryMessageDto
{
    /// <summary>The public message.</summary>
    public required CoachMessageDto Message { get; init; }

    /// <summary>
    /// The immutable position in the conversation. Strictly increasing, never reused, and the
    /// only ordering a client should trust — two messages can share a timestamp.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>The structured answer, when this message was one.</summary>
    public CoachAnswerDto? Answer { get; init; }

    /// <summary>The receipt, when this message was one.</summary>
    public CoachHistoryReceiptDto? Receipt { get; init; }

    /// <summary>The suggestion snapshot, when this message was one.</summary>
    public CoachHistorySuggestionDto? Suggestion { get; init; }

    /// <summary>The stable reason code for a notice, when this message was one.</summary>
    public string? NoticeReasonCode { get; init; }

    /// <summary>
    /// The change Sam proposed on the turn that produced this message, with its authoritative
    /// state as of this read.
    /// </summary>
    /// <remarks>
    /// This is what makes a proposal survive a reload. The card is not client state that has to be
    /// kept alive across a navigation or a refresh; it is rebuilt from the ledger every time the
    /// thread is read, in the exchange that produced it, showing whatever the server says is true
    /// now — still waiting, applied, declined, reversed, or long expired.
    /// </remarks>
    public CoachWriteOperationDto? WriteOperation { get; init; }

    /// <summary>
    /// False when the stored payload could not be read. The message still occupies its position
    /// so the thread keeps its shape instead of silently losing a turn.
    /// </summary>
    public bool IsReadable { get; init; } = true;
}

/// <summary>One page of messages, oldest first within the page.</summary>
public sealed class CoachMessagePageDto
{
    /// <summary>The conversation these messages belong to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The messages, in chronological order.</summary>
    public IReadOnlyList<CoachHistoryMessageDto> Items { get; init; } = Array.Empty<CoachHistoryMessageDto>();

    /// <summary>
    /// The cursor for the previous, older page. Null when the page reaches the start of history.
    /// </summary>
    public string? PreviousCursor { get; init; }

    /// <summary>How many messages on this page could not be read.</summary>
    public int UnreadableCount { get; init; }
}

/// <summary>
/// The state of one durable turn operation.
/// The zero value is Failed, so an unset value never shows a success state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachTurnOperationState.Failed), WireEnumFallbackKind.SafeZero,
    "Failed is the documented unset value and it keeps the learner\u2019s own message while claiming no "
    + "result. Completed would have the client read a Result that may be absent, and Running would leave "
    + "it polling an operation it can never settle.")]
public enum CoachTurnOperationState
{
    /// <summary>The turn ended with an error. The learner's message was still kept.</summary>
    Failed = 0,

    /// <summary>The turn is claimed but has not started work.</summary>
    Pending,

    /// <summary>The turn is running. Poll, or reconnect and poll.</summary>
    Running,

    /// <summary>The turn finished. The response holds the full result.</summary>
    Completed,

    /// <summary>The turn was stopped. Nothing was applied.</summary>
    Cancelled
}

/// <summary>A durable turn: its state, and its result once it has one.</summary>
/// <remarks>
/// The operation is the unit a retry addresses. The same idempotency key with the same request
/// returns this same object — including the same <see cref="Result"/> — no matter how many times
/// it is sent or how many restarts happen in between.
/// </remarks>
public sealed class CoachTurnOperationDto
{
    /// <summary>The operation identifier. Poll this to follow a turn across a reconnect.</summary>
    public required string OperationId { get; init; }

    /// <summary>The conversation the turn belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>What the operation is doing, or what it did.</summary>
    public required CoachTurnOperationState State { get; init; }

    /// <summary>True when a stop has been requested and not yet observed.</summary>
    public bool CancelRequested { get; init; }

    /// <summary>The turn result. Present once the state is <see cref="CoachTurnOperationState.Completed"/>.</summary>
    /// <remarks>
    /// This is the verbatim durable outcome, returned only on the submit path where the store can
    /// hand back what the winning worker stored. A poll reads <see cref="Messages"/> instead,
    /// because the ledger — not the operation row — is the canonical record of what was said.
    /// </remarks>
    public CoachTurnResponse? Result { get; init; }

    /// <summary>
    /// The messages this turn appended, in ledger order. Empty until the turn has appended any.
    /// </summary>
    public IReadOnlyList<CoachHistoryMessageDto> Messages { get; init; } = Array.Empty<CoachHistoryMessageDto>();

    /// <summary>The first sequence this turn appended, when it has appended any.</summary>
    public long? FirstResponseSequence { get; init; }

    /// <summary>The last sequence this turn appended, when it has appended any.</summary>
    public long? LastResponseSequence { get; init; }

    /// <summary>
    /// A stable, content-free failure code when the state is
    /// <see cref="CoachTurnOperationState.Failed"/>. Never carries learner or model text.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>The time the operation was created.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The time the operation last changed.</summary>
    public required DateTime UpdatedAtUtc { get; init; }
}

/// <summary>A turn submitted against a durable conversation.</summary>
public sealed record CoachConversationTurnRequest
{
    /// <summary>
    /// The client's retry key. Required. The same key with the same request replays the stored
    /// result; the same key with a different request is refused.
    /// </summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>
    /// The client's opaque identifier for this turn. Required, and generated by the client
    /// <em>before</em> the request is sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so a lost response is recoverable. A client that sends a turn and never sees
    /// the reply — connection dropped, app killed, phone lost signal — previously had no way to
    /// ask what happened: the operation identifier it needed to poll with only came back in the
    /// response it never received. That is a success-shaped gap, and the whole point of durable
    /// operations is not to have one.
    /// </para>
    /// <para>
    /// Because the client chooses the value up front, it can persist it alongside the pending
    /// turn and poll <c>GET /operations/{operationId}</c> after any failure, for as long as the
    /// operation is retained. The identifier is opaque and carries no meaning: the server treats
    /// it as a name, never as authority. Ownership always comes from the authenticated caller.
    /// </para>
    /// <para>
    /// It is not the idempotency key and must not be derived from one. The key is hashed and
    /// salted before storage precisely so it cannot be recovered; this is a plain handle the
    /// client is expected to keep.
    /// </para>
    /// </remarks>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>The turn itself: the same shape a session turn uses.</summary>
    public required CoachTurnRequest Turn { get; init; }
}

/// <summary>The format of a conversation export.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachExportFormat.Json), WireEnumFallbackKind.DeliberateNeutral,
    "Request-side only: the client chooses the format and the server never sends one back. Json is the "
    + "machine-readable member, so a client that somehow parsed an unknown format still asks for the "
    + "export that loses nothing.")]
public enum CoachExportFormat
{
    /// <summary>Machine-readable JSON, streamed.</summary>
    Json = 0,

    /// <summary>Human-readable Markdown, streamed.</summary>
    Markdown
}
