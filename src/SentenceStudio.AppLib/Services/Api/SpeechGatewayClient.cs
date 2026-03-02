using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Services.Api;

public sealed class SpeechGatewayClient(ISpeechApiClient speechApiClient, ILogger<SpeechGatewayClient> logger) : ISpeechGatewayClient
{
    private readonly ISpeechApiClient _speechApiClient = speechApiClient;
    private readonly ILogger<SpeechGatewayClient> _logger = logger;

    public async Task<Stream?> SynthesizeAsync(string text, string voice, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        var response = await _speechApiClient.SynthesizeAsync(
            new SynthesizeRequest
            {
                Text = text,
                VoiceId = voice
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(response.AudioUrl))
        {
            return null;
        }

        var delimiter = response.AudioUrl.IndexOf(',');
        if (delimiter < 0 || delimiter == response.AudioUrl.Length - 1)
        {
            _logger.LogWarning("Speech gateway returned audio payload in an unexpected format.");
            return null;
        }

        try
        {
            var base64 = response.AudioUrl[(delimiter + 1)..];
            var bytes = Convert.FromBase64String(base64);
            return new MemoryStream(bytes, writable: false);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Speech gateway returned invalid base64 audio data.");
            return null;
        }
    }
}
