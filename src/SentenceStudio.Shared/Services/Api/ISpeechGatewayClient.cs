namespace SentenceStudio.Services.Api;

public interface ISpeechGatewayClient
{
    Task<Stream?> SynthesizeAsync(string text, string voice, float speed = 1.0f, CancellationToken cancellationToken = default);
}
