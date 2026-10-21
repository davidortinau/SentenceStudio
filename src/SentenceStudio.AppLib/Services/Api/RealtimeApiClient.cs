using System.Net.Http;
using System.Net.Http.Json;
using SentenceStudio.Contracts.Ai;

namespace SentenceStudio.Services.Api;

public sealed class RealtimeApiClient(HttpClient httpClient) : IRealtimeApiClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<RealtimeClientSecretResponse> CreateClientSecretAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync("/api/v1/realtime/client-secret", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RealtimeClientSecretResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The API returned no realtime client secret.");
    }
}
