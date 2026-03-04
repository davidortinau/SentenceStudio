using SentenceStudio.Contracts.Ai;

namespace SentenceStudio.Services.Api;

public interface IAiApiClient
{
    Task<ChatResponse> SendChatAsync(ChatRequest request, CancellationToken cancellationToken = default);
    Task<ChatResponse> SendChatMessagesAsync(ChatMessagesRequest request, CancellationToken cancellationToken = default);
    Task<ChatResponse> AnalyzeImageAsync(AnalyzeImageRequest request, CancellationToken cancellationToken = default);
}
