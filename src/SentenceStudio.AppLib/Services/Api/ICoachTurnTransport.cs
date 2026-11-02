using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Services.Api;

/// <summary>Optional host-specific transport for coach runs; other resources keep their REST contracts.</summary>
public interface ICoachTurnTransport
{
    Task<CoachTurnResponse> SubmitTurnAsync(
        string sessionId, CoachTurnRequest request, CancellationToken cancellationToken);

    Task<CoachTurnOperationDto> SubmitConversationTurnAsync(
        string conversationId, CoachConversationTurnRequest request, CancellationToken cancellationToken);
}
