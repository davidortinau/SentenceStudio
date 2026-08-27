namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// The versions durable history stamps onto every row it writes.
/// </summary>
/// <remarks>
/// These are code constants, not operator knobs. An operator cannot make an unreadable payload
/// readable by editing configuration, and a stored row always records the version it was
/// written under so a later build can project it without guessing.
/// </remarks>
public static class CoachHistorySchema
{
    /// <summary>The current conversation metadata shape.</summary>
    public const int ConversationMetadataVersion = 1;

    /// <summary>The current <see cref="CoachMessagePayload"/> contract.</summary>
    public const int MessagePayloadVersion = 1;

    /// <summary>The current durable turn-outcome payload contract.</summary>
    public const int TurnOutcomeVersion = 1;
}
