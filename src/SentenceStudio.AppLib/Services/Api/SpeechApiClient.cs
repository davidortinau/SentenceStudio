using System.Net.Http;
using System.Net.Http.Json;
using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Services.Api;

public sealed class SpeechApiClient(HttpClient httpClient) : ISpeechApiClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/speech/synthesize", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SynthesizeResponse>(cancellationToken: cancellationToken)
            ?? new SynthesizeResponse();
    }
}
