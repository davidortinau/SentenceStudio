namespace SentenceStudio.Contracts.Conversation;

/// <summary>
/// Body for <c>POST /api/v1/conversation/start</c>.
/// All JSON keys serialize as camelCase via the minimal-API web defaults.
/// </summary>
public sealed class ConversationStartRequest
{
    /// <summary>
    /// Optional scenario id; when omitted, a free-chat opening is used.
    /// </summary>
    public int? ScenarioId { get; set; }

    /// <summary>
    /// BCP-47 tag (e.g. "ko", "ko-KR") or the human label (e.g. "Korean").
    /// </summary>
    public string? TargetLanguage { get; set; }
}
