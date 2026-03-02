namespace SentenceStudio.Contracts.Ai;

public sealed class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? Scenario { get; set; }
    public string? ResponseType { get; set; }
}
