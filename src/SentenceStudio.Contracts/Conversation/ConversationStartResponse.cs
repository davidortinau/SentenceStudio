namespace SentenceStudio.Contracts.Conversation;

/// <summary>
/// Body for <c>POST /api/v1/conversation/start</c>.
/// Keys serialize as camelCase via the minimal-API web defaults.
/// </summary>
public sealed class ConversationStartResponse
{
    public string FirstAssistantMessage { get; set; } = string.Empty;
    public string? PersonaName { get; set; }
    public string ConversationType { get; set; } = "OpenEnded";
}
