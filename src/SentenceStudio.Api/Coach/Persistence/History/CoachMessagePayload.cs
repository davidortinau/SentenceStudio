using System.Text.Json;
using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// What a stored message payload carries.
/// </summary>
/// <remarks>
/// Serialized <b>by name</b>, so a member can be reordered without re-labelling stored rows.
/// This is the payload's own discriminator and is intentionally separate from
/// <see cref="CoachMessageKind"/>: the kind says how the client renders the row, this says which
/// payload branch is populated.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachMessagePayloadKind
{
    /// <summary>Text the learner typed.</summary>
    LearnerText = 0,

    /// <summary>Plain coach text, including clarification questions.</summary>
    CoachText,

    /// <summary>A structured language-learning answer.</summary>
    StructuredAnswer,

    /// <summary>A status notice such as a limit or an error.</summary>
    Notice,

    /// <summary>A receipt for a plan change the learner accepted.</summary>
    Receipt,

    /// <summary>A snapshot of a suggestion as the learner saw it.</summary>
    SuggestionSnapshot
}

/// <summary>One run of text inside a stored answer block.</summary>
public sealed class CoachStoredAnswerSpan
{
    /// <summary>Plain visible text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Which of the learner's languages this run is written in.</summary>
    public CoachLanguageRole Language { get; set; }

    /// <summary>The server-resolved BCP-47 tag for <see cref="Language"/>.</summary>
    public string LanguageTag { get; set; } = string.Empty;
}

/// <summary>One labelled part of a stored answer.</summary>
public sealed class CoachStoredAnswerBlock
{
    /// <summary>The role this block plays.</summary>
    public CoachAnswerBlockKind Kind { get; set; }

    /// <summary>An optional short heading in the display language.</summary>
    public string? Label { get; set; }

    /// <summary>The block's text, in order.</summary>
    public List<CoachStoredAnswerSpan> Spans { get; set; } = new();
}

/// <summary>A stored language-learning answer, reduced to what the learner saw.</summary>
public sealed class CoachStoredAnswer
{
    /// <summary>What the answer is about.</summary>
    public CoachAnswerTopic Topic { get; set; }

    /// <summary>The answer, in order.</summary>
    public List<CoachStoredAnswerBlock> Blocks { get; set; } = new();

    /// <summary>The flattened rendering for clients that cannot render blocks.</summary>
    public string PlainText { get; set; } = string.Empty;

    /// <summary>The BCP-47 tag of the language being studied.</summary>
    public string TargetLanguageTag { get; set; } = string.Empty;

    /// <summary>The BCP-47 tag the explanation is written in.</summary>
    public string DisplayLanguageTag { get; set; } = string.Empty;

    /// <summary>True when the answer ends with a recall question.</summary>
    public bool EndsWithRecallQuestion { get; set; }
}

/// <summary>A stored status notice.</summary>
public sealed class CoachStoredNotice
{
    /// <summary>A closed, content-free reason code the client maps to localized copy.</summary>
    /// <remarks>
    /// Must be a member of <see cref="CoachNoticeReasonCodes.All"/> on every new write; the empty
    /// default exists so an unset code fails validation rather than persisting as a blank.
    /// </remarks>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>The localized notice text the learner saw.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// A stored receipt for an applied plan change.
/// </summary>
/// <remarks>
/// Deliberately reduced from the live receipt DTO. The applied delta, the plan diff, and the
/// vocabulary focus are omitted because they carry internal identifiers — including vocabulary
/// item ids — that the visible ledger has no business retaining. The learner sees the summary
/// and the change lines; the plan revision audit remains the record of what actually changed.
/// </remarks>
public sealed class CoachStoredReceipt
{
    /// <summary>The receipt identifier the message refers to.</summary>
    public string ReceiptId { get; set; } = string.Empty;

    /// <summary>The plan revision the change produced.</summary>
    public string? RevisionId { get; set; }

    /// <summary>The localized one-line summary the learner saw.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The localized human-readable change lines, in display order.</summary>
    public List<string> ChangeLines { get; set; } = new();
}

/// <summary>
/// A stored snapshot of a suggestion, as it was presented.
/// </summary>
/// <remarks>
/// Only the learner-visible surface is kept: the rationale, the action labels, and the change
/// lines. The constraint delta, plan preview, evidence rows, and frozen vocabulary selection are
/// not stored, because they carry internal identifiers and re-deriving them from a stale snapshot
/// would be misleading anyway.
/// </remarks>
public sealed class CoachStoredSuggestionSnapshot
{
    /// <summary>The suggestion identifier the message refers to.</summary>
    public string SuggestionId { get; set; } = string.Empty;

    /// <summary>The localized reason shown with the suggestion.</summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>The localized change lines shown with the suggestion.</summary>
    public List<string> ChangeLines { get; set; } = new();

    /// <summary>The localized accept action label.</summary>
    public string? AcceptLabel { get; set; }

    /// <summary>The localized reject action label.</summary>
    public string? RejectLabel { get; set; }
}

/// <summary>
/// The typed envelope stored — encrypted — in <see cref="CoachMessage.ProtectedPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the persistence contract, deliberately decoupled from the wire DTOs. The wire shapes
/// will keep moving as the coach product evolves; a ledger row written last month must still
/// project correctly after they do, so the stored shape versions independently via
/// <see cref="SchemaVersion"/>.
/// </para>
/// <para>
/// <b>Never stored:</b> chain-of-thought, developer or system instructions, model prompts, tool
/// arguments or results, internal vocabulary identifiers, plan item identifiers, agent-session
/// state, provider traces, keys, or token accounting. Only what the learner is entitled to read
/// back belongs here.
/// </para>
/// </remarks>
public sealed class CoachMessagePayload
{
    /// <summary>The payload contract version this instance was written under.</summary>
    public int SchemaVersion { get; set; } = CoachHistorySchema.MessagePayloadVersion;

    /// <summary>Which payload branch is populated.</summary>
    public CoachMessagePayloadKind Kind { get; set; }

    /// <summary>
    /// The canonical timestamp the learner saw. Duplicated from the row so an exported payload
    /// stays self-describing.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// The primary visible text for every kind. For structured payloads this is the flattened
    /// rendering, so a client that cannot render the branch still shows the right words.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The suggestion this message refers to, when any.</summary>
    public string? RelatedSuggestionId { get; set; }

    /// <summary>The receipt this message refers to, when any.</summary>
    public string? RelatedReceiptId { get; set; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="CoachMessagePayloadKind.StructuredAnswer"/>.</summary>
    public CoachStoredAnswer? Answer { get; set; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="CoachMessagePayloadKind.Notice"/>.</summary>
    public CoachStoredNotice? Notice { get; set; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="CoachMessagePayloadKind.Receipt"/>.</summary>
    public CoachStoredReceipt? Receipt { get; set; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="CoachMessagePayloadKind.SuggestionSnapshot"/>.</summary>
    public CoachStoredSuggestionSnapshot? Suggestion { get; set; }
}

/// <summary>Why a payload was rejected before it reached the database.</summary>
public enum CoachPayloadValidationError
{
    /// <summary>The payload is valid.</summary>
    None = 0,

    /// <summary>A visible text field exceeded its bound.</summary>
    TextTooLong,

    /// <summary>Too many blocks, spans, or lines.</summary>
    TooManyElements,

    /// <summary>The serialized payload exceeded the byte bound.</summary>
    PayloadTooLarge,

    /// <summary>The branch required by <see cref="CoachMessagePayload.Kind"/> was missing.</summary>
    MissingBranch,

    /// <summary>
    /// A notice arrived with an empty reason code, or one outside the shared vocabulary.
    /// </summary>
    /// <remarks>
    /// New writes only. A row already on disk carrying a code this build does not recognize stays
    /// readable — the read path never validates — because refusing to render a learner's own history
    /// is a worse outcome than rendering a notice whose marker cannot be derived.
    /// </remarks>
    InvalidReasonCode
}

/// <summary>The outcome of bounding a payload.</summary>
/// <param name="Error">Why it was rejected, or <see cref="CoachPayloadValidationError.None"/>.</param>
/// <param name="Field">The offending field name. Never a value, so this is safe to log.</param>
public readonly record struct CoachPayloadValidationResult(CoachPayloadValidationError Error, string? Field = null)
{
    /// <summary>True when the payload may be serialized and protected.</summary>
    public bool IsValid => Error == CoachPayloadValidationError.None;

    /// <summary>The success result.</summary>
    public static CoachPayloadValidationResult Ok => new(CoachPayloadValidationError.None);
}

/// <summary>
/// Serializes, bounds, and deserializes <see cref="CoachMessagePayload"/>.
/// </summary>
/// <remarks>
/// Bounds are enforced on the plaintext, before protection. Encryption hides content but not
/// length, so validating afterwards would leave the size limit to be discovered by the database.
/// </remarks>
public static class CoachMessagePayloadSerializer
{
    /// <summary>
    /// Deterministic options. Enums serialize by name so ordinal drift cannot re-label stored
    /// payloads, and nulls are dropped so an unpopulated branch costs nothing.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Serializes a bounded payload. Throws when the payload is out of bounds.</summary>
    public static string Serialize(CoachMessagePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var validation = Validate(payload);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Coach message payload rejected: {validation.Error} ({validation.Field ?? "payload"}).");
        }

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Deserializes a stored payload. Returns false for anything unreadable.</summary>
    public static bool TryDeserialize(string? json, out CoachMessagePayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<CoachMessagePayload>(json, Options);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Bounds a payload without serializing it for storage.</summary>
    public static CoachPayloadValidationResult Validate(CoachMessagePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Text.Length > CoachHistoryLimits.TextMaxLength)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(payload.Text));
        }

        var branchResult = payload.Kind switch
        {
            CoachMessagePayloadKind.StructuredAnswer => ValidateAnswer(payload.Answer),
            CoachMessagePayloadKind.Notice => ValidateNotice(payload.Notice),
            CoachMessagePayloadKind.Receipt => ValidateReceipt(payload.Receipt),
            CoachMessagePayloadKind.SuggestionSnapshot => ValidateSuggestion(payload.Suggestion),
            _ => CoachPayloadValidationResult.Ok
        };

        if (!branchResult.IsValid)
        {
            return branchResult;
        }

        // Size is checked last, on the exact bytes that would be encrypted.
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload, Options));
        return byteCount > CoachHistoryLimits.MessagePayloadMaxBytes
            ? new CoachPayloadValidationResult(CoachPayloadValidationError.PayloadTooLarge, nameof(payload))
            : CoachPayloadValidationResult.Ok;
    }

    private static CoachPayloadValidationResult ValidateAnswer(CoachStoredAnswer? answer)
    {
        if (answer is null)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.MissingBranch, nameof(CoachMessagePayload.Answer));
        }

        if (answer.PlainText.Length > CoachHistoryLimits.TextMaxLength)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(answer.PlainText));
        }

        if (answer.Blocks.Count > CoachHistoryLimits.AnswerBlockMax)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.TooManyElements, nameof(answer.Blocks));
        }

        foreach (var block in answer.Blocks)
        {
            if (block.Spans.Count > CoachHistoryLimits.AnswerSpanMax)
            {
                return new CoachPayloadValidationResult(CoachPayloadValidationError.TooManyElements, nameof(block.Spans));
            }

            if (block.Label is { Length: > CoachHistoryLimits.LineMaxLength })
            {
                return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(block.Label));
            }

            foreach (var span in block.Spans)
            {
                if (span.Text.Length > CoachHistoryLimits.AnswerSpanTextMaxLength)
                {
                    return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(span.Text));
                }
            }
        }

        return CoachPayloadValidationResult.Ok;
    }

    private static CoachPayloadValidationResult ValidateNotice(CoachStoredNotice? notice)
    {
        if (notice is null)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.MissingBranch, nameof(CoachMessagePayload.Notice));
        }

        if (notice.ReasonCode.Length > CoachHistoryLimits.ErrorCodeMaxLength)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(notice.ReasonCode));
        }

        // Closed-set on the way in. An empty code would read back as a malformed record, and an
        // invented one would leave every client silent about whether the plan moved; both are
        // cheaper to refuse here than to discover in someone's history months later.
        if (!CoachNoticeReasonCodes.IsKnown(notice.ReasonCode))
        {
            return new CoachPayloadValidationResult(
                CoachPayloadValidationError.InvalidReasonCode,
                nameof(notice.ReasonCode));
        }

        return notice.Text.Length > CoachHistoryLimits.TextMaxLength
            ? new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(notice.Text))
            : CoachPayloadValidationResult.Ok;
    }

    private static CoachPayloadValidationResult ValidateReceipt(CoachStoredReceipt? receipt)
    {
        if (receipt is null)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.MissingBranch, nameof(CoachMessagePayload.Receipt));
        }

        if (receipt.Summary.Length > CoachHistoryLimits.TextMaxLength)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(receipt.Summary));
        }

        return ValidateLines(receipt.ChangeLines, nameof(receipt.ChangeLines));
    }

    private static CoachPayloadValidationResult ValidateSuggestion(CoachStoredSuggestionSnapshot? suggestion)
    {
        if (suggestion is null)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.MissingBranch, nameof(CoachMessagePayload.Suggestion));
        }

        if (suggestion.Rationale.Length > CoachHistoryLimits.TextMaxLength)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, nameof(suggestion.Rationale));
        }

        return ValidateLines(suggestion.ChangeLines, nameof(suggestion.ChangeLines));
    }

    private static CoachPayloadValidationResult ValidateLines(List<string> lines, string field)
    {
        if (lines.Count > CoachHistoryLimits.SuggestionLineMax)
        {
            return new CoachPayloadValidationResult(CoachPayloadValidationError.TooManyElements, field);
        }

        foreach (var line in lines)
        {
            if (line.Length > CoachHistoryLimits.LineMaxLength)
            {
                return new CoachPayloadValidationResult(CoachPayloadValidationError.TextTooLong, field);
            }
        }

        return CoachPayloadValidationResult.Ok;
    }
}
