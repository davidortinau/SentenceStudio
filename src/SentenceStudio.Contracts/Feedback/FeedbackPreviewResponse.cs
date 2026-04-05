namespace SentenceStudio.Contracts.Feedback;

public sealed class FeedbackPreviewResponse
{
    public string Title { get; set; } = string.Empty;
    public string FormattedBody { get; set; } = string.Empty;
    public string[] Labels { get; set; } = [];
    public string FeedbackType { get; set; } = string.Empty;
    public string PreviewToken { get; set; } = string.Empty;
}
