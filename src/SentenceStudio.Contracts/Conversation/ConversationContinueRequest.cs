using System.Collections.Generic;

namespace SentenceStudio.Contracts.Conversation;

/// <summary>
/// Body for <c>POST /api/v1/conversation/continue</c>.
/// History is oldest -> newest; the last entry must be the user's new turn.
/// </summary>
public sealed class ConversationContinueRequest
{
    public int? ScenarioId { get; set; }
    public string? TargetLanguage { get; set; }
    public List<ConversationHistoryItemDto> History { get; set; } = new();
}
