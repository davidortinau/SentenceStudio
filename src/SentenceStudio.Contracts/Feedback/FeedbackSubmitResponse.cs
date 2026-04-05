namespace SentenceStudio.Contracts.Feedback;

public sealed class FeedbackSubmitResponse
{
    public string IssueUrl { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string Title { get; set; } = string.Empty;
}
