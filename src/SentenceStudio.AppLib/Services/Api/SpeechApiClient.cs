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

    public async Task<IReadOnlyList<SpeechApiVoice>> GetVoicesAsync(string language, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            "/api/v1/speech/voices?language=" + Uri.EscapeDataString(language), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<VoiceListBody>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The voices endpoint returned no body.");
        return body.Voices ?? throw new InvalidOperationException("The voices endpoint returned no voice list.");
    }

    private sealed record VoiceListBody(IReadOnlyList<SpeechApiVoice>? Voices);
}

public sealed class TimestampedSpeechApiClient(HttpClient httpClient) : ITimestampedSpeechApiClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<SynthesizeTimestampedResponse> SynthesizeTimestampedAsync(
        SynthesizeTimestampedRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/speech/synthesize-timestamped", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SynthesizeTimestampedResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The timestamped speech endpoint returned no body.");
    }
}
