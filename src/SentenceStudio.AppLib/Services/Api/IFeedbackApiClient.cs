using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Services.Api;

/// <summary>
/// Why a feedback call did not return a result. A closed set, so the page can say something
/// specific without parsing prose out of a problem response.
/// </summary>
public enum FeedbackApiFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>A per-owner limit refused it. <see cref="FeedbackApiResult{T}.RetryAfter"/> is set.</summary>
    RateLimited = 1,

    /// <summary>The preview token was refused. The learner has to write the report again.</summary>
    TokenRejected = 2,

    /// <summary>
    /// A submission for this preview is already under way, or its outcome is unknown. The client
    /// must not retry — that is the whole point of the state.
    /// </summary>
    InDoubt = 3,

    /// <summary>Something else went wrong and a retry is reasonable.</summary>
    Unavailable = 4,

    /// <summary>
    /// The submission closed without creating an issue, and the server knows that rather than
    /// assuming it. The preview is spent; the learner writes the report again.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="InDoubt"/> because the two are indistinguishable by status code and
    /// their honest messages are opposites. Telling a learner to "check GitHub before writing it
    /// again" when nothing was filed sends them looking for something that does not exist and
    /// leaves them unsure whether they reported the bug at all — the opposite of the certainty the
    /// exactly-once design is for.
    /// </remarks>
    Closed = 5
}

/// <summary>The outcome of a feedback call.</summary>
/// <remarks>
/// A result type rather than exceptions, because two of the failures above carry information the
/// page must act on — a wait, and a "do not retry" — and an exception with a message in it is
/// where that information goes to die.
/// </remarks>
public sealed class FeedbackApiResult<T> where T : class
{
    private FeedbackApiResult(T? value, FeedbackApiFailure failure, TimeSpan? retryAfter)
    {
        Value = value;
        Failure = failure;
        RetryAfter = retryAfter;
    }

    /// <summary>The response, when the call succeeded.</summary>
    public T? Value { get; }

    /// <summary>Why it did not, when it did not.</summary>
    public FeedbackApiFailure Failure { get; }

    /// <summary>How long to wait, when the server said so. Never invented by the client.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>True when <see cref="Value"/> is present.</summary>
    public bool Succeeded => Failure == FeedbackApiFailure.None && Value is not null;

    public static FeedbackApiResult<T> Success(T value) => new(value, FeedbackApiFailure.None, null);

    public static FeedbackApiResult<T> Failed(FeedbackApiFailure failure, TimeSpan? retryAfter = null) =>
        new(null, failure, retryAfter);
}

public interface IFeedbackApiClient
{
    Task<FeedbackApiResult<FeedbackPreviewResponse>> PreviewAsync(
        FeedbackRequest request, CancellationToken cancellationToken = default);

    Task<FeedbackApiResult<FeedbackSubmitResponse>> SubmitAsync(
        FeedbackSubmitRequest request, CancellationToken cancellationToken = default);
}
