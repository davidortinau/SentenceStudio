using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Services.Api;

public interface ISpeechApiClient
{
    Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken = default);
}
