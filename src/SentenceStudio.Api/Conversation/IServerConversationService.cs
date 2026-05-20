using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SentenceStudio.Contracts.Conversation;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Conversation;

/// <summary>
/// Stateless server-side analogue of <c>IConversationAgentService</c>.
/// Each request is fully self-contained: the client passes scenario + full
/// history every turn. See spec 004 §Implementation Note 8.
/// </summary>
public interface IServerConversationService
{
    /// <summary>
    /// Generates the opening assistant message for a (possibly null) scenario.
    /// </summary>
    Task<string> StartAsync(
        ConversationScenario? scenario,
        string targetLanguageLabel,
        CancellationToken cancellationToken);

    /// <summary>
    /// Continues the conversation with the user's latest turn. Returns the
    /// assistant reply plus grading results (already in wire shape).
    /// </summary>
    Task<ConversationContinueResponse> ContinueAsync(
        string userMessage,
        IReadOnlyList<ConversationHistoryItemDto> conversationHistory,
        ConversationScenario? scenario,
        string targetLanguageLabel,
        CancellationToken cancellationToken);
}
