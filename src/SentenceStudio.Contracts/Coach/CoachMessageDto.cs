namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// One message in the coach conversation.
/// The server localizes the coach text. The learner text stays as the learner typed it.
/// </summary>
public sealed class CoachMessageDto
{
    /// <summary>The message identifier.</summary>
    public required string MessageId { get; init; }

    /// <summary>Who wrote the message.</summary>
    public required CoachMessageRole Role { get; init; }

    /// <summary>The display role of the message.</summary>
    public required CoachMessageKind Kind { get; init; }

    /// <summary>The message text.</summary>
    public required string Text { get; init; }

    /// <summary>The time the server recorded the message.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The suggestion this message refers to. Null if the message refers to no suggestion.</summary>
    public string? RelatedSuggestionId { get; init; }

    /// <summary>The receipt this message refers to. Null if the message refers to no receipt.</summary>
    public string? RelatedReceiptId { get; init; }
}
