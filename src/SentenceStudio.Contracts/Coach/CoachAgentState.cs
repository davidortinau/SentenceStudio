namespace SentenceStudio.Contracts.Coach;

/// <summary>The application-owned payload carried by an AG-UI run.</summary>
public sealed class CoachAgentState
{
    public CoachTurnRequest? Turn { get; init; }
    public CoachConversationTurnRequest? DurableTurn { get; init; }
    public CoachTurnResponse? Result { get; init; }
    public CoachTurnOperationDto? Operation { get; init; }
}
