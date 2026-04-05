using System.Net.Http;
using System.Net.Http.Json;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Services.Api;

public sealed class FeedbackApiClient(HttpClient httpClient) : IFeedbackApiClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<FeedbackPreviewResponse> PreviewAsync(FeedbackRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/feedback/preview", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FeedbackPreviewResponse>(cancellationToken: cancellationToken)
            ?? new FeedbackPreviewResponse();
    }

    public async Task<FeedbackSubmitResponse> SubmitAsync(FeedbackSubmitRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/feedback/submit", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FeedbackSubmitResponse>(cancellationToken: cancellationToken)
            ?? new FeedbackSubmitResponse();
    }
}
