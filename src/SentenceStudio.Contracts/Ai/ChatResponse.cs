namespace SentenceStudio.Contracts.Ai;

public sealed class ChatResponse
{
    public string Response { get; set; } = string.Empty;
    public string? Language { get; set; }
}
