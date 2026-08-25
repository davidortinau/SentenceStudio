namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// One run of text inside an answer block, tagged with the language it is written in.
/// </summary>
/// <remarks>
/// Spans exist so a client can render target-language text in the right script, font, and
/// speech voice without guessing where one language stops and the next begins. The tag is a
/// BCP-47 language tag the <b>server</b> resolved from the learner's profile.
/// </remarks>
public sealed class CoachAnswerSpanDto
{
    /// <summary>Plain text. No markup, and never longer than the span limit.</summary>
    public required string Text { get; init; }

    /// <summary>Which of the learner's languages this run is written in.</summary>
    public required CoachLanguageRole Language { get; init; }

    /// <summary>The server-resolved BCP-47 tag for <see cref="Language"/>, for example "ko-KR".</summary>
    public required string LanguageTag { get; init; }
}

/// <summary>One labelled part of an answer: the direct answer, an example, a contrast, and so on.</summary>
public sealed class CoachAnswerBlockDto
{
    /// <summary>The role this block plays.</summary>
    public required CoachAnswerBlockKind Kind { get; init; }

    /// <summary>An optional short heading in the display language.</summary>
    public string? Label { get; init; }

    /// <summary>The block's text, in order.</summary>
    public IReadOnlyList<CoachAnswerSpanDto> Spans { get; init; } = Array.Empty<CoachAnswerSpanDto>();
}

/// <summary>
/// A language-learning answer. Present only on a turn that answered a question; it never
/// accompanies a plan write.
/// </summary>
/// <remarks>
/// <para>
/// This carries no learner identity, no due-item list, no diary or transcript content, and no
/// assessment answer. Every span is scanned against the learner's embargoed items before the
/// answer leaves the server.
/// </para>
/// <para>
/// <see cref="PlainText"/> is a flattened copy for clients that cannot render blocks. It is
/// derived from the same spans and is scanned with them, so it can never carry text the blocks
/// do not.
/// </para>
/// </remarks>
public sealed class CoachAnswerDto
{
    /// <summary>What the answer is about.</summary>
    public required CoachAnswerTopic Topic { get; init; }

    /// <summary>The answer, in order. The first block is always the direct answer.</summary>
    public IReadOnlyList<CoachAnswerBlockDto> Blocks { get; init; } = Array.Empty<CoachAnswerBlockDto>();

    /// <summary>A flattened plain-text rendering of <see cref="Blocks"/>.</summary>
    public required string PlainText { get; init; }

    /// <summary>The server-resolved BCP-47 tag of the language being studied.</summary>
    public required string TargetLanguageTag { get; init; }

    /// <summary>The server-resolved BCP-47 tag the explanation is written in.</summary>
    public required string DisplayLanguageTag { get; init; }

    /// <summary>
    /// True when the answer ends with a question inviting the learner to recall the point.
    /// </summary>
    /// <remarks>
    /// Named for what it describes rather than for the block kind, because the coach contract
    /// scan bans "prompt" on a public property so a model prompt can never surface on one.
    /// </remarks>
    public bool EndsWithRecallQuestion { get; init; }
}
