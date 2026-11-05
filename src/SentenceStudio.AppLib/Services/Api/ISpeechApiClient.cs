using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Services.Api;

public interface ISpeechApiClient
{
    Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET /api/v1/speech/voices?language=</c>: the voices the API offers for a language name
    /// ("Korean") or BCP-47 tag. Throws <see cref="HttpRequestException"/> when the API refuses.
    /// </summary>
    Task<IReadOnlyList<SpeechApiVoice>> GetVoicesAsync(string language, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>POST /api/v1/speech/synthesize-timestamped</c>. A separate client because a long transcript
/// can take minutes and is a paid call: it has a five-minute timeout and is never retried.
/// </summary>
public interface ITimestampedSpeechApiClient
{
    /// <summary>Throws <see cref="HttpRequestException"/> (with its status) when the API refuses.</summary>
    Task<SynthesizeTimestampedResponse> SynthesizeTimestampedAsync(
        SynthesizeTimestampedRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One voice from <c>GET /api/v1/speech/voices</c>.</summary>
public sealed record SpeechApiVoice(string Id, string Name, string Language, string? Gender, string? Accent);
