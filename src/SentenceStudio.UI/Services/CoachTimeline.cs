using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.WebUI.Services;

/// <summary>What one entry in the conversation timeline is.</summary>
public enum CoachTimelineKind
{
    /// <summary>Something the learner submitted.</summary>
    LearnerMessage = 0,

    /// <summary>Something Sam said, including a structured pedagogical answer.</summary>
    CoachMessage,

    /// <summary>A receipt for an applied plan change.</summary>
    Receipt,

    /// <summary>Where the pending suggestion for this turn belongs in the stream.</summary>
    SuggestionAnchor,

    /// <summary>
    /// The record of a suggestion that was offered earlier, replayed from durable history.
    /// </summary>
    /// <remarks>
    /// Deliberately a different kind from <see cref="SuggestionAnchor"/>. A replayed suggestion is
    /// a transcript of an offer that has already been answered; rendering it through the live
    /// anchor would put Accept and Reject buttons on a decision the learner made days ago.
    /// </remarks>
    HistorySuggestion,

    /// <summary>
    /// A message whose stored payload could not be read back, standing in for the real one.
    /// </summary>
    /// <remarks>
    /// The slot is kept rather than dropped so the thread keeps its shape: a learner who counts
    /// six exchanges should still see six, with one of them plainly marked as lost, instead of a
    /// transcript that quietly rewrites what happened.
    /// </remarks>
    UnreadableMessage,

    /// <summary>
    /// Marks the point before which durable history is no longer retained.
    /// </summary>
    /// <remarks>
    /// Rendered only when the conversation reports a retention boundary. Without it, the oldest
    /// retained message reads as the beginning of the thread, which is a claim the server never
    /// made.
    /// </remarks>
    HistoryBoundary,

    /// <summary>
    /// A message whose kind this build does not recognise, standing in for the real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="UnreadableMessage"/> on purpose. Unreadable means the stored
    /// payload failed to decode — the content is genuinely lost. Unsupported means the content
    /// arrived intact and this build cannot be trusted to present it: a newer server named a
    /// message kind that has no case here, and the missing case is very likely the controls that
    /// made the message safe to act on. Telling the two apart is what lets the copy be honest
    /// ("update the app") instead of alarming ("your message was lost").
    /// </para>
    /// <para>
    /// The slot is kept rather than dropped, for the same reason the unreadable slot is: a
    /// transcript that quietly omits a turn is a transcript that rewrites what happened.
    /// </para>
    /// </remarks>
    UnsupportedMessage
}

/// <summary>
/// How settled an entry is. Only learner messages are ever unsettled: they are the one thing the
/// UI shows before the server has confirmed it.
/// </summary>
public enum CoachTimelineStatus
{
    /// <summary>Confirmed — either the server produced it, or the server has acknowledged it.</summary>
    Settled = 0,

    /// <summary>Shown optimistically; the turn that carries it is still in flight.</summary>
    Pending,

    /// <summary>
    /// The turn failed. The learner's own words stay on screen and stay retryable, because
    /// deleting what someone typed is a worse outcome than showing that it did not send.
    /// </summary>
    Failed
}

/// <summary>
/// One artifact in the conversation, placed by an explicit sequence rather than by its type.
/// </summary>
/// <remarks>
/// <para>
/// The conversation is a single chronological stream. Grouping by role or by type is what
/// produced the reported transcript — two learner questions, then an answer, then a receipt —
/// because receipts and suggestions rendered after every message regardless of which turn had
/// produced them.
/// </para>
/// <para>
/// Ordering is <see cref="TurnSequence"/> then <see cref="Sequence"/>, so a slow response lands
/// beside the question that asked for it even if a later question was submitted first. Both are
/// client-allocated and monotonic; neither is ever reused.
/// </para>
/// </remarks>
public sealed class CoachTimelineEntry
{
    /// <summary>The turn this artifact belongs to. Every artifact of one exchange shares it.</summary>
    public required long TurnSequence { get; init; }

    /// <summary>Position within the turn. The learner's own message is always first.</summary>
    public required long Sequence { get; init; }

    /// <summary>What this entry is.</summary>
    public required CoachTimelineKind Kind { get; init; }

    /// <summary>
    /// When this artifact appeared.
    /// </summary>
    /// <remarks>
    /// Client capture time for anything produced during the active circuit, and the server's own
    /// stamp for anything that carries one. The type is a <see cref="DateTimeOffset"/> rather than
    /// a local <see cref="DateTime"/> precisely so a server timestamp can replace a captured one
    /// later without changing any rendering code.
    /// </remarks>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The message, for learner and coach entries.</summary>
    public CoachMessageDto? Message { get; init; }

    /// <summary>The structured answer paired to this message, when there is one.</summary>
    public CoachAnswerDto? Answer { get; init; }

    /// <summary>
    /// The read-only facts the turn that produced this message drew on, when it drew on any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried on the entry rather than beside the timeline, for the same reason the write
    /// proposal is: evidence belongs to the exchange that cited it. Holding one workspace-wide
    /// list meant every one of Sam's messages advertised the newest turn's evidence, including
    /// the messages that had cited nothing at all — a control offered on a claim it did not
    /// belong to.
    /// </para>
    /// <para>
    /// Empty is the normal case and the honest one. Durable history does not carry per-turn
    /// evidence, so a message read back after a reload has none here and offers no disclosure —
    /// the plan canvas still shows the evidence behind the current plan, which is a claim the
    /// server does make.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CoachEvidenceDto> Evidence { get; init; } = Array.Empty<CoachEvidenceDto>();

    /// <summary>
    /// What the grounding layer did to <em>this</em> answer, when it did anything worth saying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried on the entry for the same reason <see cref="Evidence"/> is, and it is the same
    /// defect being closed twice. A single workspace-wide disclosure rendered at the head of the
    /// log, which put "part of this answer was adjusted" above a thread the learner had already
    /// scrolled past — off screen after auto-scroll, and attached to no answer in particular. A
    /// disclosure that is not beside the sentence it describes is a disclosure about nothing.
    /// </para>
    /// <para>
    /// Null is the ordinary case. <c>None</c> is never stored here: checked-and-clean is not news,
    /// and the attach path drops it rather than making every renderer re-derive that.
    /// </para>
    /// </remarks>
    public CoachRepairDisclosure? RepairDisclosure { get; init; }

    /// <summary>
    /// Whether the turn that produced <see cref="RepairDisclosure"/> put evidence on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turn-scoped, and deliberately not read from <see cref="Evidence"/> at render time. Two of
    /// the disclosure states point the learner at the evidence, and pointing at evidence that is
    /// not there is the defect this closes: the workspace evidence list is sticky by design — an
    /// ordinary turn that cites nothing leaves the previous turn's rows standing, because the
    /// learner may still be reading them — so "is there evidence anywhere" was answering a
    /// question nobody asked. This answers "did the turn this disclosure describes read anything",
    /// which is the only one the copy can honestly depend on.
    /// </para>
    /// <para>
    /// False whenever there is no disclosure, and meaningless in that case.
    /// </para>
    /// </remarks>
    public bool RepairEvidenceOnScreen { get; init; }

    /// <summary>The receipt, for receipt entries.</summary>
    public CoachChangeReceiptDto? Receipt { get; init; }

    // ------------------------------------------------------------- durable history

    /// <summary>
    /// The identity this entry merges on: the durable message id, or the client's own
    /// <c>local:</c> handle for a learner message the server has not confirmed yet.
    /// </summary>
    /// <remarks>
    /// Merging on identity rather than on position is what stops a reload from doubling the
    /// thread. Text is not an identity — a learner who says "yes" twice said it twice.
    /// </remarks>
    public string? MessageId { get; init; }

    /// <summary>
    /// The server's own ordinal within the conversation, when this entry came from durable
    /// history.
    /// </summary>
    /// <remarks>
    /// The only trustworthy order. Two messages can share a timestamp to the millisecond, and a
    /// client clock is not evidence of anything, so the sequence — not the stamp — decides what
    /// comes first.
    /// </remarks>
    public long? ServerSequence { get; init; }

    /// <summary>
    /// The turn this entry arrived on, when it was folded in from the ledger while that turn was
    /// running. Null on every other entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TurnSequence"/> cannot answer "which turn produced this" on a durable thread.
    /// It is a read-order ordinal there, reassigned by every merge, because the timeline is
    /// presented in the server's order and arrival order stops being the server's order the moment
    /// an older page is fetched. So a turn's own artifacts — its evidence, and the note about what
    /// was done to its answer — had nothing to match against, and rendered nowhere at all until a
    /// reload rebuilt the thread from scratch.
    /// </para>
    /// <para>
    /// This is set only where the client knows the answer for certain: the merge of the rows an
    /// operation carried while the client was waiting on it. A transcript load and a page of older
    /// history both leave it null, because neither is a turn and neither may claim to be one.
    /// It survives renumbering by design — the ordinal is where the entry is read, this is where
    /// it came from, and only the first of those changes when the thread is re-sorted.
    /// </para>
    /// </remarks>
    public long? ArrivedOnTurn { get; init; }

    /// <summary>Whether the server has confirmed this entry. Only learner messages are ever not.</summary>
    public CoachTimelineStatus Status { get; init; } = CoachTimelineStatus.Settled;

    /// <summary>The durable record of a receipt, which carries less than the live one.</summary>
    public CoachHistoryReceiptDto? HistoryReceipt { get; init; }

    /// <summary>The durable record of a suggestion that was offered. Never re-appliable.</summary>
    public CoachHistorySuggestionDto? HistorySuggestion { get; init; }

    /// <summary>
    /// The change Sam proposed on this turn, with the state the server last reported for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried on the entry rather than held beside the timeline, because a proposal belongs to
    /// the exchange that produced it. Holding it separately is what produces a card stacked at the
    /// top of a thread, describing something the learner has to scroll to find the context for.
    /// </para>
    /// <para>
    /// Replaced wholesale after every approval action with whatever the server answered, never
    /// patched in place. A card that shows "applied" because a request returned 200 is a card that
    /// will eventually lie.
    /// </para>
    /// </remarks>
    public CoachWriteOperationDto? WriteOperation { get; init; }

    /// <summary>
    /// The closed-vocabulary reason code explaining a notice. Present on every notice.
    /// </summary>
    /// <remarks>
    /// Stamped by the server on durable rows and stamped with the identical helper on session-only
    /// rows, so the same outcome carries the same code in both modes. It is never inferred from what
    /// Sam wrote.
    /// </remarks>
    public string? NoticeReasonCode { get; init; }

    /// <summary>
    /// True when the authoritative turn outcome says this notice recorded no plan change.
    /// </summary>
    /// <remarks>
    /// An alias for reading <see cref="NoticeReasonCode"/> through the shared vocabulary. Kept as a
    /// named member because "did this turn leave the plan alone" is the question callers actually
    /// ask; the code is the storage, not the concept.
    /// </remarks>
    public bool IsNoChangeNotice => CoachNoticeReasonCodes.IndicatesNoChange(NoticeReasonCode);

    /// <summary>
    /// True when this entry must show the "no change applied" marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One predicate for both modes, over one field. Every notice carries a reason code, so a
    /// non-null code cannot mean "refusal" — an informational notice and a receipt-bearing turn both
    /// carry <see cref="CoachNoticeReasonCodes.Default"/> and must stay unmarked. Membership in the
    /// closed refusal set is the only thing that marks.
    /// </para>
    /// <para>
    /// The "a turn that still wrote something is not a no-change turn" rule is resolved on the
    /// server when the code is stamped, because durable history renumbers rows and a client cannot
    /// pair a notice with its sibling receipt.
    /// </para>
    /// </remarks>
    public bool ShowsNoChangeMarker => CoachNoticeReasonCodes.IndicatesNoChange(NoticeReasonCode);

    /// <summary>True when this entry is a learner message that has not settled yet.</summary>
    public bool IsUnsettled => Status is not CoachTimelineStatus.Settled;

    /// <summary>True when this entry is something Sam said, as opposed to a plan artifact.</summary>
    public bool IsConversational =>
        Kind is CoachTimelineKind.CoachMessage or CoachTimelineKind.LearnerMessage;

    /// <summary>
    /// The readable text of this entry, in the order a learner reads it.
    /// </summary>
    /// <remarks>
    /// Structured answers are flattened block by block, span by span, so a copy carries the same
    /// content in the same order as the screen. Identifiers, language tags and markup are not
    /// part of what was said and never appear.
    /// </remarks>
    public string ReadableText()
    {
        if (Answer is { } answer)
        {
            var blocks = answer.Blocks
                .Select(block => string.Concat(block.Spans.Select(s => s.Text)).Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            if (blocks.Count > 0)
            {
                return string.Join("\n\n", blocks);
            }

            if (!string.IsNullOrWhiteSpace(answer.PlainText))
            {
                return answer.PlainText.Trim();
            }
        }

        return Message?.Text?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Returns this entry re-stated as the server records it: canonical id, sequence and stamp,
    /// and settled.
    /// </summary>
    /// <remarks>
    /// Reconciliation replaces rather than mutates so that a render in flight can never observe
    /// an entry that is half local and half canonical. The learner's text is taken from the
    /// server copy when it has one, because the server is what the next reload will show.
    /// </remarks>
    public CoachTimelineEntry Reconciled(CoachHistoryMessageDto durable) => new()
    {
        TurnSequence = TurnSequence,
        Sequence = Sequence,
        Kind = Kind,
        Timestamp = ServerTime(durable.Message.CreatedAtUtc),
        Message = durable.Message,
        Answer = durable.Answer ?? Answer,
        Evidence = Evidence,
        Receipt = Receipt,
        RepairDisclosure = RepairDisclosure,
        RepairEvidenceOnScreen = RepairEvidenceOnScreen,
        MessageId = durable.Message.MessageId,
        ServerSequence = durable.Sequence,
        ArrivedOnTurn = ArrivedOnTurn,
        Status = CoachTimelineStatus.Settled,
        HistoryReceipt = durable.Receipt,
        HistorySuggestion = durable.Suggestion,
        // The ledger's answer wins whenever history carries one, because it is read fresh on
        // every page load; keeping a local copy would show a stale card after a reload.
        WriteOperation = durable.WriteOperation ?? WriteOperation,
        // The durable row wins when it carries a code. A durable row that is not a notice has no
        // code at all, and dropping the local one there would silently unmark a settled refusal.
        NoticeReasonCode = durable.NoticeReasonCode ?? NoticeReasonCode
    };

    /// <summary>
    /// Reads a server stamp as the local wall-clock time the learner was actually at.
    /// </summary>
    /// <remarks>
    /// The wire carries UTC, but a <see cref="DateTime"/> whose Kind is Unspecified converts to a
    /// <see cref="DateTimeOffset"/> by assuming it is already local. That is how a message sent at
    /// 11:52 PM came back reading 4:52 AM: nothing was lost, the value was simply relabelled with
    /// the wrong offset. Pinning the Kind before converting is what makes a durable message and
    /// the optimistic copy it replaces show the same time.
    /// </remarks>
    public static DateTimeOffset ServerTime(DateTime createdAtUtc) =>
        createdAtUtc == default
            ? DateTimeOffset.Now
            : new DateTimeOffset(DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc)).ToLocalTime();

    /// <summary>
    /// The timeline slot a message belongs in, from the two facts the server sent about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The unsupported check comes first, before the role.</b> A message this build cannot
    /// classify must render as a neutral placeholder no matter who sent it: the missing case is
    /// most likely the one that carried the controls, and a suggestion or a consent prompt shown
    /// as plain prose is a decision the learner cannot see they are being asked to make.
    /// </para>
    /// <para>
    /// Both places a message enters the timeline — the live turn and the durable page — go through
    /// here, so the two cannot disagree about which messages are safe to render.
    /// </para>
    /// </remarks>
    public static CoachTimelineKind KindFor(CoachMessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Kind == CoachMessageKind.Unrecognized)
        {
            return CoachTimelineKind.UnsupportedMessage;
        }

        return message.Role == CoachMessageRole.Learner
            ? CoachTimelineKind.LearnerMessage
            : CoachTimelineKind.CoachMessage;
    }

    /// <summary>
    /// Returns this entry moved to a new position in the read order, with its content untouched.
    /// </summary>
    /// <remarks>
    /// The timeline is presented in <see cref="TurnSequence"/> then <see cref="Sequence"/> order,
    /// and those counters are assigned as entries arrive. That is the wrong order the moment an
    /// older page is fetched: messages that happened first arrive last. Renumbering by the
    /// server's own sequence is what makes "load earlier" put earlier messages earlier.
    /// </remarks>
    public CoachTimelineEntry Renumbered(long ordinal) => new()
    {
        TurnSequence = ordinal,
        Sequence = ordinal,
        Kind = Kind,
        Timestamp = Timestamp,
        Message = Message,
        Answer = Answer,
        Evidence = Evidence,
        Receipt = Receipt,
        RepairDisclosure = RepairDisclosure,
        RepairEvidenceOnScreen = RepairEvidenceOnScreen,
        MessageId = MessageId,
        ServerSequence = ServerSequence,
        ArrivedOnTurn = ArrivedOnTurn,
        Status = Status,
        HistoryReceipt = HistoryReceipt,
        HistorySuggestion = HistorySuggestion,
        NoticeReasonCode = NoticeReasonCode,
        WriteOperation = WriteOperation
    };

    /// <summary>Returns this entry carrying a structured answer, with nothing else changed.</summary>
    public CoachTimelineEntry WithAnswer(CoachAnswerDto answer) => new()
    {
        TurnSequence = TurnSequence,
        Sequence = Sequence,
        Kind = Kind,
        Timestamp = Timestamp,
        Message = Message,
        Answer = answer,
        Evidence = Evidence,
        Receipt = Receipt,
        RepairDisclosure = RepairDisclosure,
        RepairEvidenceOnScreen = RepairEvidenceOnScreen,
        MessageId = MessageId,
        ServerSequence = ServerSequence,
        ArrivedOnTurn = ArrivedOnTurn,
        Status = Status,
        HistoryReceipt = HistoryReceipt,
        HistorySuggestion = HistorySuggestion,
        NoticeReasonCode = NoticeReasonCode,
        WriteOperation = WriteOperation
    };

    /// <summary>
    /// Returns this entry carrying the given proposal state, with nothing else changed.
    /// </summary>
    /// <remarks>
    /// Used after an approval, a decline, or a reversal, so the card re-renders from the state the
    /// server just reported rather than from what the client guessed the action would do.
    /// </remarks>
    public CoachTimelineEntry WithWriteOperation(CoachWriteOperationDto? writeOperation) => new()
    {
        TurnSequence = TurnSequence,
        Sequence = Sequence,
        Kind = Kind,
        Timestamp = Timestamp,
        Message = Message,
        Answer = Answer,
        Evidence = Evidence,
        Receipt = Receipt,
        RepairDisclosure = RepairDisclosure,
        RepairEvidenceOnScreen = RepairEvidenceOnScreen,
        MessageId = MessageId,
        ServerSequence = ServerSequence,
        ArrivedOnTurn = ArrivedOnTurn,
        Status = Status,
        HistoryReceipt = HistoryReceipt,
        HistorySuggestion = HistorySuggestion,
        NoticeReasonCode = NoticeReasonCode,
        WriteOperation = writeOperation
    };

    /// <summary>
    /// Returns this entry carrying the evidence its turn drew on, with nothing else changed.
    /// </summary>
    /// <remarks>
    /// Applied to the message that actually made the claim, so the disclosure sits under the
    /// sentence it explains rather than under whichever of Sam's messages happens to be last.
    /// </remarks>
    public CoachTimelineEntry WithEvidence(IReadOnlyList<CoachEvidenceDto> evidence) => new()
    {
        TurnSequence = TurnSequence,
        Sequence = Sequence,
        Kind = Kind,
        Timestamp = Timestamp,
        Message = Message,
        Answer = Answer,
        Evidence = evidence,
        Receipt = Receipt,
        RepairDisclosure = RepairDisclosure,
        RepairEvidenceOnScreen = RepairEvidenceOnScreen,
        MessageId = MessageId,
        ServerSequence = ServerSequence,
        ArrivedOnTurn = ArrivedOnTurn,
        Status = Status,
        HistoryReceipt = HistoryReceipt,
        HistorySuggestion = HistorySuggestion,
        NoticeReasonCode = NoticeReasonCode,
        WriteOperation = WriteOperation
    };

    /// <summary>
    /// Returns this entry carrying what the grounding layer did to its answer, with nothing else
    /// changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied to the message that carries the answer being described, so the note sits under that
    /// answer rather than at the head of the log. The head of the log is where it used to sit, and
    /// on any thread longer than a screen the learner never saw it: the pane auto-scrolls to the
    /// newest message and the notice stayed at the top, describing an answer several screens away.
    /// </para>
    /// <para>
    /// The evidence flag is passed in rather than derived from <see cref="Evidence"/> because the
    /// two disclosure states that point at evidence must point at <em>this</em> turn's evidence,
    /// and only the caller applying the turn knows whether the turn read anything.
    /// </para>
    /// </remarks>
    public CoachTimelineEntry WithRepairDisclosure(
        CoachRepairDisclosure disclosure,
        bool evidenceOnScreen) => new()
    {
        TurnSequence = TurnSequence,
        Sequence = Sequence,
        Kind = Kind,
        Timestamp = Timestamp,
        Message = Message,
        Answer = Answer,
        Evidence = Evidence,
        Receipt = Receipt,
        RepairDisclosure = disclosure,
        RepairEvidenceOnScreen = evidenceOnScreen,
        MessageId = MessageId,
        ServerSequence = ServerSequence,
        ArrivedOnTurn = ArrivedOnTurn,
        Status = Status,
        HistoryReceipt = HistoryReceipt,
        HistorySuggestion = HistorySuggestion,
        NoticeReasonCode = NoticeReasonCode,
        WriteOperation = WriteOperation
    };

    /// <summary>Returns this entry with a different settle status and nothing else changed.</summary>
    public CoachTimelineEntry WithStatus(CoachTimelineStatus status) => new()    {
        TurnSequence = TurnSequence,
        Sequence = Sequence,
        Kind = Kind,
        Timestamp = Timestamp,
        Message = Message,
        Answer = Answer,
        Evidence = Evidence,
        Receipt = Receipt,
        RepairDisclosure = RepairDisclosure,
        RepairEvidenceOnScreen = RepairEvidenceOnScreen,
        MessageId = MessageId,
        ServerSequence = ServerSequence,
        ArrivedOnTurn = ArrivedOnTurn,
        Status = status,
        HistoryReceipt = HistoryReceipt,
        HistorySuggestion = HistorySuggestion,
        NoticeReasonCode = NoticeReasonCode,
        WriteOperation = WriteOperation
    };
}
