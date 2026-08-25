namespace SentenceStudio.Contracts.Feedback;

/// <summary>
/// Whether this response is the issue being created, or the authoritative record of one that
/// already was.
/// </summary>
/// <remarks>
/// Submission is exactly-once. A retry, a double-click, or a second tab presenting the same
/// preview token does not create a second public issue — it is answered with the receipt of the
/// first. The client needs to be able to tell those apart so it can say "filed" once rather than
/// implying two issues exist. Members may only be appended.
/// </remarks>
public enum FeedbackSubmitOutcome
{
    /// <summary>This request is the one that created the issue.</summary>
    Created = 0,

    /// <summary>The issue already existed; this is the stored receipt for it.</summary>
    Replayed = 1
}

public sealed class FeedbackSubmitResponse
{
    public string IssueUrl { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Whether this request created the issue or replayed an existing receipt.</summary>
    public FeedbackSubmitOutcome Outcome { get; set; } = FeedbackSubmitOutcome.Created;
}
