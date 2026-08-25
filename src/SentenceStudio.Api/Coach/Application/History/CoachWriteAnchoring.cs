using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// Decides which message a turn's proposed change renders under.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism that keeps a proposal card inside the exchange that produced it. The
/// pairing is exact rather than heuristic: a message row records the durable turn operation that
/// wrote it, a write proposal records the turn it was proposed in, and those are the same
/// identifier because the turn pipeline hands the write scope the operation's own id.
/// </para>
/// <para>
/// Extracted from the read path so the rule can be tested on its own. A card in the wrong place is
/// not a crash and not a failed request — it is a decision presented next to the wrong sentence,
/// which is exactly the class of bug an integration test discovers late and a unit test discovers
/// immediately.
/// </para>
/// </remarks>
public static class CoachWriteAnchoring
{
    /// <summary>
    /// Maps each proposal onto the index of the message it renders under.
    /// </summary>
    /// <param name="records">The page of stored rows, in ledger order.</param>
    /// <param name="writes">The proposals whose turns may appear on that page.</param>
    /// <returns>
    /// Message index to proposal. A proposal whose turn is not on this page is absent, because a
    /// card with no context to sit in is worse than no card.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The anchor is the <em>last</em> message of the turn that Sam wrote, so the card reads after
    /// what Sam said rather than beside the learner's question.
    /// </para>
    /// <para>
    /// One proposal per turn is a ledger invariant, enforced before a row is written
    /// (<c>CoachWriteLimits.ProposalsPerTurnMax</c>), so the last-write-wins assignment below is
    /// reached by at most one proposal per anchor. It is kept as written rather than tightened
    /// into a throw because rows recorded before that invariant existed are still readable, and a
    /// page of old history that showed the newest of two is a smaller failure than a page that
    /// refused to render at all.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<int, CoachWriteOperationDto> ByMessage(
        IReadOnlyList<CoachMessageRecord> records,
        IReadOnlyList<CoachWriteOperationDto> writes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(writes);

        var paired = new Dictionary<int, CoachWriteOperationDto>();
        if (records.Count == 0 || writes.Count == 0)
        {
            return paired;
        }

        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].OperationId is { Length: > 0 } operationId
                && records[i].Role == CoachMessageRole.Coach)
            {
                anchors[operationId] = i;
            }
        }

        foreach (var write in writes)
        {
            if (write.TurnId is { Length: > 0 } turnId && anchors.TryGetValue(turnId, out var index))
            {
                paired[index] = write;
            }
        }

        return paired;
    }

    /// <summary>Returns the proposal stamped with the message it renders under.</summary>
    /// <remarks>
    /// The anchor is assigned here rather than in the ledger because only the read path knows
    /// which message a turn's proposal ended up beside; the ledger records the turn, not the
    /// transcript.
    /// </remarks>
    public static CoachWriteOperationDto Anchored(CoachWriteOperationDto write, string messageId)
    {
        ArgumentNullException.ThrowIfNull(write);

        return new CoachWriteOperationDto
        {
            OperationId = write.OperationId,
            ConversationId = write.ConversationId,
            TurnId = write.TurnId,
            MessageId = messageId,
            ChangeKind = write.ChangeKind,
            RiskClass = write.RiskClass,
            Status = write.Status,
            ApprovalMode = write.ApprovalMode,
            Summary = write.Summary,
            Lines = write.Lines,
            ExpiresAtUtc = write.ExpiresAtUtc,
            RequiresConfirmation = write.RequiresConfirmation,
            ConfirmationExpiresAtUtc = write.ConfirmationExpiresAtUtc,
            IsReversible = write.IsReversible,
            IsDuplicate = write.IsDuplicate,
            AlreadyExecuted = write.AlreadyExecuted,
            Receipt = write.Receipt
        };
    }
}
