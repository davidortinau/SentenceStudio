using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Services.Api;

/// <summary>
/// <c>POST /api/v1/speech/synthesize</c>. Each call is one attempt (no retries): an attempt the API
/// does not serve from its cache is a paid generation.
/// </summary>
public interface ISpeechApiClient
{
    Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>GET /api/v1/speech/voices</c>, an idempotent read: its client keeps the standard resilience
/// pipeline (retries and timeouts), unlike the synthesis clients.
/// </summary>
public interface ISpeechVoicesApiClient
{
    /// <summary>
    /// The voices the API offers for a language name ("Korean") or BCP-47 tag. Throws
    /// <see cref="HttpRequestException"/> when the API refuses.
    /// </summary>
    Task<IReadOnlyList<SpeechApiVoice>> GetVoicesAsync(string language, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>POST /api/v1/speech/synthesize-timestamped</c>. A long transcript can take minutes and is a paid
/// call: five-minute timeout, never retried.
/// </summary>
public interface ITimestampedSpeechApiClient
{
    /// <summary>Throws <see cref="HttpRequestException"/> (with its status) when the API refuses.</summary>
    Task<SynthesizeTimestampedResponse> SynthesizeTimestampedAsync(
        SynthesizeTimestampedRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One voice from <c>GET /api/v1/speech/voices</c>.</summary>
public sealed record SpeechApiVoice(string Id, string Name, string Language, string? Gender, string? Accent);
