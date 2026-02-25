using System.Net.Http;
using System.Net.Http.Json;
using SentenceStudio.Contracts.Ai;

namespace SentenceStudio.Services.Api;

public sealed class AiApiClient(HttpClient httpClient) : IAiApiClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<ChatResponse> SendChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/ai/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: cancellationToken)
            ?? new ChatResponse();
    }
}
