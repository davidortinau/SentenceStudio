namespace SentenceStudio.Contracts.Feedback;

public sealed class FeedbackRequest
{
    public string Description { get; set; } = string.Empty;
    public string? FeedbackType { get; set; }
    public ClientMetadata? ClientMetadata { get; set; }
}

public sealed class ClientMetadata
{
    public string? AppVersion { get; set; }
    public string? Platform { get; set; }
    public string? CurrentRoute { get; set; }
    public DateTime? Timestamp { get; set; }
}
