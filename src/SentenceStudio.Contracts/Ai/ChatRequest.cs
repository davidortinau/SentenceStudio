namespace SentenceStudio.Contracts.Ai;

public sealed class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? Scenario { get; set; }
    public string? ResponseType { get; set; }

    /// <summary>
    /// Model tier to run on ("Fast" or "Reasoning"). Null/unknown defaults to the fast tier.
    /// Sent as a string to keep Contracts free of a dependency on the AiTier enum.
    /// </summary>
    public string? Tier { get; set; }

    /// <summary>
    /// Optional OpenAI reasoning effort override ("minimal", "low", "medium", or "high").
    /// Null preserves the provider/model default.
    /// </summary>
    public string? ReasoningEffort { get; set; }
}
