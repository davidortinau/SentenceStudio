using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// Projects between the durable message ledger and the public coach contracts.
/// </summary>
/// <remarks>
/// <para>
/// One direction turns a finished turn into rows the ledger can keep; the other turns rows back
/// into the exact shapes a client already renders. Both live here so the two halves cannot drift:
/// a field added to the stored payload without a projection would silently vanish from history,
/// and a field projected without being stored would silently appear empty after a restart.
/// </para>
/// <para>
/// <b>What never crosses.</b> Model prompts, chain-of-thought, tool arguments and results, agent
/// session state, plan item and vocabulary identifiers, constraint deltas, and plan diffs are not
/// projected into the ledger in either direction. The ledger is the record of what the learner
/// was shown; the plan revision audit remains the record of what actually changed.
/// </para>
/// </remarks>
public static class CoachHistoryProjection
{
    /// <summary>The text shown in place of a message whose payload could not be decrypted.</summary>
    public const string UnreadablePlaceholder = "\u2014";

    /// <summary>The reason code used when a notice arrives without a more specific one.</summary>
    public const string DefaultNoticeReasonCode = CoachNoticeReasonCodes.Default;

    /// <summary>
    /// Builds the payload for the learner's own message, written before the model is called.
    /// </summary>
    /// <remarks>
    /// Appended first and independently of the outcome, so a failed turn still leaves the learner
    /// looking at what they typed rather than at a thread that swallowed it.
    /// </remarks>
    public static CoachMessagePayload LearnerMessage(string text, DateTime createdAtUtc) =>
        new()
        {
            Kind = CoachMessagePayloadKind.LearnerText,
            CreatedAtUtc = createdAtUtc,
            Text = Clamp(text, CoachHistoryLimits.TextMaxLength)
        };

    /// <summary>
    /// Turns one finished turn into the rows the ledger should hold, in the exact order the
    /// learner saw them.
    /// </summary>
    /// <remarks>
    /// Driven by <see cref="CoachTurnResponse.Messages"/> rather than by the structured members,
    /// because the message list is what the client renders and is therefore the only ordering
    /// that can be replayed faithfully. The structured members are attached to the message that
    /// referenced them.
    /// </remarks>
    public static IReadOnlyList<CoachMessagePayload> ResponseMessages(CoachTurnResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var payloads = new List<CoachMessagePayload>(response.Messages.Count);

        foreach (var message in response.Messages)
        {
            // The learner's own message is appended before the model runs, so echoing it here
            // would duplicate it in the ledger.
            if (message.Role == CoachMessageRole.Learner)
            {
                continue;
            }

            payloads.Add(FromMessage(message, response));
        }

        return payloads;
    }

    private static CoachMessagePayload FromMessage(CoachMessageDto message, CoachTurnResponse response)
    {
        var payload = new CoachMessagePayload
        {
            CreatedAtUtc = message.CreatedAtUtc,
            Text = Clamp(message.Text, CoachHistoryLimits.TextMaxLength),
            RelatedSuggestionId = message.RelatedSuggestionId,
            RelatedReceiptId = message.RelatedReceiptId
        };

        switch (message.Kind)
        {
            case CoachMessageKind.PedagogicalAnswer when response.Answer is not null:
                payload.Kind = CoachMessagePayloadKind.StructuredAnswer;
                payload.Answer = StoreAnswer(response.Answer);
                break;

            case CoachMessageKind.Receipt when response.ChangeReceipt is not null:
                payload.Kind = CoachMessagePayloadKind.Receipt;
                payload.Receipt = StoreReceipt(response.ChangeReceipt);
                break;

            case CoachMessageKind.Suggestion when response.PendingSuggestion is not null:
                payload.Kind = CoachMessagePayloadKind.SuggestionSnapshot;
                payload.Suggestion = StoreSuggestion(response.PendingSuggestion);
                break;

            case CoachMessageKind.Notice:
                payload.Kind = CoachMessagePayloadKind.Notice;
                payload.Notice = new CoachStoredNotice
                {
                    // Resolved here, while the turn is still whole. History renumbers every stored
                    // row into its own ordinal, so a client reading this notice back cannot tell
                    // whether the same turn also wrote a receipt.
                    ReasonCode = CoachNoticeReasonCodes.ForNotice(
                        response.StopReason,
                        response.ChangeReceipt is not null),
                    Text = payload.Text
                };
                break;

            default:
                // Includes a Receipt, Suggestion, or Answer message whose structured member did
                // not arrive: the visible text is still true and is kept rather than dropped.
                payload.Kind = CoachMessagePayloadKind.CoachText;
                break;
        }

        return payload;
    }

    /// <summary>The wire kind a stored payload renders as.</summary>
    public static CoachMessageKind KindFor(CoachMessagePayloadKind payloadKind) => payloadKind switch
    {
        CoachMessagePayloadKind.StructuredAnswer => CoachMessageKind.PedagogicalAnswer,
        CoachMessagePayloadKind.Receipt => CoachMessageKind.Receipt,
        CoachMessagePayloadKind.SuggestionSnapshot => CoachMessageKind.Suggestion,
        CoachMessagePayloadKind.Notice => CoachMessageKind.Notice,
        _ => CoachMessageKind.Text
    };

    /// <summary>Projects one stored row back to the public history shape.</summary>
    /// <param name="record">The stored row.</param>
    /// <param name="writeOperation">
    /// The change the turn that produced this message proposed, when the caller has already
    /// looked it up. Passed in rather than fetched here because the ledger lives behind an
    /// owner-scoped service and a projection must stay a pure function of what it is handed.
    /// </param>
    public static CoachHistoryMessageDto ToHistoryMessage(
        CoachMessageRecord record,
        CoachWriteOperationDto? writeOperation = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        var payload = record.Payload;

        var message = new CoachMessageDto
        {
            MessageId = record.Id,
            Role = record.Role,
            Kind = record.Kind,
            Text = payload?.Text ?? UnreadablePlaceholder,
            CreatedAtUtc = record.CreatedAt,
            RelatedSuggestionId = payload?.RelatedSuggestionId,
            RelatedReceiptId = payload?.RelatedReceiptId
        };

        if (payload is null)
        {
            return new CoachHistoryMessageDto
            {
                Message = message,
                Sequence = record.Sequence,
                WriteOperation = writeOperation,
                IsReadable = false
            };
        }

        return new CoachHistoryMessageDto
        {
            Message = message,
            Sequence = record.Sequence,
            Answer = payload.Answer is null ? null : ToAnswer(payload.Answer),
            Receipt = payload.Receipt is null ? null : ToReceipt(payload.Receipt),
            Suggestion = payload.Suggestion is null ? null : ToSuggestion(payload.Suggestion),
            NoticeReasonCode = payload.Notice?.ReasonCode,
            WriteOperation = writeOperation,
            IsReadable = true
        };
    }

    private static CoachStoredAnswer StoreAnswer(CoachAnswerDto answer) => new()
    {
        Topic = answer.Topic,
        PlainText = Clamp(answer.PlainText, CoachHistoryLimits.TextMaxLength),
        TargetLanguageTag = answer.TargetLanguageTag,
        DisplayLanguageTag = answer.DisplayLanguageTag,
        EndsWithRecallQuestion = answer.EndsWithRecallQuestion,
        Blocks = answer.Blocks
            .Take(CoachHistoryLimits.AnswerBlockMax)
            .Select(block => new CoachStoredAnswerBlock
            {
                Kind = block.Kind,
                Label = block.Label,
                Spans = block.Spans
                    .Take(CoachHistoryLimits.AnswerSpanMax)
                    .Select(span => new CoachStoredAnswerSpan
                    {
                        Text = Clamp(span.Text, CoachHistoryLimits.AnswerSpanTextMaxLength),
                        Language = span.Language,
                        LanguageTag = span.LanguageTag
                    })
                    .ToList()
            })
            .ToList()
    };

    private static CoachAnswerDto ToAnswer(CoachStoredAnswer stored) => new()
    {
        Topic = stored.Topic,
        PlainText = stored.PlainText,
        TargetLanguageTag = stored.TargetLanguageTag,
        DisplayLanguageTag = stored.DisplayLanguageTag,
        EndsWithRecallQuestion = stored.EndsWithRecallQuestion,
        Blocks = stored.Blocks
            .Select(block => new CoachAnswerBlockDto
            {
                Kind = block.Kind,
                Label = block.Label,
                Spans = block.Spans
                    .Select(span => new CoachAnswerSpanDto
                    {
                        Text = span.Text,
                        Language = span.Language,
                        LanguageTag = span.LanguageTag
                    })
                    .ToList()
            })
            .ToList()
    };

    private static CoachStoredReceipt StoreReceipt(CoachChangeReceiptDto receipt) => new()
    {
        ReceiptId = receipt.ReceiptId,
        RevisionId = receipt.Revision.RevisionId,
        Summary = Clamp(receipt.Summary, CoachHistoryLimits.TextMaxLength),
        // The diff itself is not stored — only the lines the learner was shown about it.
        ChangeLines = ReceiptLines(receipt)
    };

    private static List<string> ReceiptLines(CoachChangeReceiptDto receipt)
    {
        var lines = new List<string>(CoachHistoryLimits.SuggestionLineMax);

        if (receipt.ReplacedItemCount > 0)
        {
            lines.Add($"Replaced {receipt.ReplacedItemCount} unfinished items");
        }

        if (receipt.PreservedCompletedItemCount > 0)
        {
            lines.Add($"Kept {receipt.PreservedCompletedItemCount} completed items");
        }

        if (receipt.PreservedInProgressItemCount > 0)
        {
            lines.Add($"Kept {receipt.PreservedInProgressItemCount} started items");
        }

        if (receipt.PreservedMinutesSpent > 0)
        {
            lines.Add($"Kept {receipt.PreservedMinutesSpent} logged minutes");
        }

        return lines
            .Take(CoachHistoryLimits.SuggestionLineMax)
            .Select(line => Clamp(line, CoachHistoryLimits.LineMaxLength))
            .ToList();
    }

    private static CoachHistoryReceiptDto ToReceipt(CoachStoredReceipt stored) => new()
    {
        ReceiptId = stored.ReceiptId,
        RevisionId = stored.RevisionId ?? string.Empty,
        Summary = stored.Summary,
        ChangeLines = stored.ChangeLines.ToList()
    };

    private static CoachStoredSuggestionSnapshot StoreSuggestion(PendingCoachSuggestionDto suggestion) => new()
    {
        SuggestionId = suggestion.SuggestionId,
        Rationale = Clamp(suggestion.Rationale, CoachHistoryLimits.TextMaxLength),
        AcceptLabel = suggestion.AcceptLabel,
        RejectLabel = suggestion.RejectLabel,
        // Evidence rows carry internal identifiers and stale counts; only the localized reason
        // the learner read is kept.
        ChangeLines = new List<string>()
    };

    private static CoachHistorySuggestionDto ToSuggestion(CoachStoredSuggestionSnapshot stored) => new()
    {
        SuggestionId = stored.SuggestionId,
        Rationale = stored.Rationale,
        ChangeLines = stored.ChangeLines.ToList(),
        AcceptLabel = stored.AcceptLabel,
        RejectLabel = stored.RejectLabel
    };

    /// <summary>Maps a stop reason to the closed notice code stored with a notice message.</summary>
    /// <remarks>
    /// Names the stop reason only. A notice being written should use
    /// <see cref="CoachNoticeReasonCodes.ForNotice"/> instead, which also accounts for a turn that
    /// stopped badly but still produced a change.
    /// </remarks>
    public static string NoticeReasonCode(CoachStopReason reason) =>
        CoachNoticeReasonCodes.FromStopReason(reason);

    /// <summary>Projects a conversation row to the public shape.</summary>
    /// <summary>
    /// A ledger record as the legacy <see cref="CoachMessageDto"/> the <c>/sessions</c> routes
    /// return, or null when the record has no place in that older shape.
    /// </summary>
    /// <remarks>
    /// The compatibility surface predates structured history, so it carries role, kind, text, and
    /// time and nothing else. An unreadable record is dropped rather than rendered as a
    /// placeholder: the older client has no way to show "this could not be decrypted", and a
    /// blank bubble would read as something the coach actually said.
    /// </remarks>
    public static CoachMessageDto? ToSessionMessage(CoachMessageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!record.IsReadable || record.Payload is null)
        {
            return null;
        }

        var text = record.Payload.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new CoachMessageDto
        {
            // The same identity the durable surface returns for this row. It used to be the ledger
            // sequence, which meant one message answered to two different ids depending on which
            // endpoint the client asked, and a client merging a turn response with a reloaded page
            // could not tell that they were the same message. One message, one id, every surface.
            MessageId = record.Id,
            Role = record.Role,
            Kind = record.Kind,
            Text = text,
            CreatedAtUtc = record.CreatedAt
        };
    }

    public static CoachConversationDto ToConversation(CoachConversationRecord record, bool hasActiveCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new CoachConversationDto
        {
            ConversationId = record.Id,
            Title = record.IsTitleReadable && !string.IsNullOrWhiteSpace(record.Title)
                ? record.Title!
                : UnreadablePlaceholder,
            TitleOrigin = record.IsTitleReadable
                ? record.TitleSource == CoachConversationTitleSource.Learner
                    ? CoachConversationTitleOrigin.Learner
                    : CoachConversationTitleOrigin.Generated
                : CoachConversationTitleOrigin.Unreadable,
            TargetLanguageCode = record.TargetLanguageCode,
            CreatedAtUtc = record.CreatedAt,
            UpdatedAtUtc = record.UpdatedAt,
            HistoryStartsAtUtc = record.HistoryStartsAt,
            MessageCount = record.LastSequence,
            StateVersion = record.Version,
            HasActiveCheckpoint = hasActiveCheckpoint,
            IsClosed = record.Status == CoachConversationStatus.Closed
        };
    }

    private static string Clamp(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }
}
