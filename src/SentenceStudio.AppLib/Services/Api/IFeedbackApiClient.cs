using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Services.Api;

public interface IFeedbackApiClient
{
    Task<FeedbackPreviewResponse> PreviewAsync(FeedbackRequest request, CancellationToken cancellationToken = default);
    Task<FeedbackSubmitResponse> SubmitAsync(FeedbackSubmitRequest request, CancellationToken cancellationToken = default);
}
