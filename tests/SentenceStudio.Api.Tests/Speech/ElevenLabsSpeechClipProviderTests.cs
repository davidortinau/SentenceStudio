using System.Net;
using System.Text;
using System.Text.Json;
using ElevenLabs;
using SentenceStudio.Api.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The provider sends ElevenLabs exactly what the spec says, and nothing else that could change the
/// audio. That is what makes a hash of the spec a complete cache key. No network: the SDK's
/// HttpClient is answered in process.
/// </summary>
public sealed class ElevenLabsSpeechClipProviderTests
{
    [Fact]
    public async Task Synthesize_request_is_built_only_from_the_spec()
    {
        var handler = new RecordingHandler(_ => AudioResponse([1, 2, 3]));
        var provider = new ElevenLabsSpeechClipProvider(Client(handler));
        var spec = SpeechClipSpec.ForSynthesize("voice-123", "eleven_flash_v2_5", "안녕하세요");

        var audio = await provider.GenerateAsync(spec, CancellationToken.None);

        audio.Should().Equal(1, 2, 3);
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/v1/text-to-speech/voice-123");
        request.Query.Should().Contain("output_format=mp3_44100_128");

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        root.GetProperty("text").GetString().Should().Be("안녕하세요");
        root.GetProperty("model_id").GetString().Should().Be("eleven_flash_v2_5");
        var settings = root.GetProperty("voice_settings");
        settings.GetProperty("stability").GetSingle().Should().Be(spec.Stability);
        settings.GetProperty("similarity_boost").GetSingle().Should().Be(spec.SimilarityBoost);
        settings.GetProperty("style").GetSingle().Should().Be(spec.Style);
        settings.GetProperty("use_speaker_boost").GetBoolean().Should().Be(spec.SpeakerBoost);
        settings.GetProperty("speed").GetSingle().Should().Be(spec.Speed);

        // Every other body field is empty, so nothing outside the spec shapes the audio.
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "text" or "model_id" or "voice_settings")
            {
                continue;
            }

            property.Value.ValueKind.Should().Be(JsonValueKind.Null,
                $"'{property.Name}' is not in the cache key, so the provider must not set it");
        }
    }

    [Fact]
    public async Task Timestamped_request_uses_the_spec_settings_and_returns_millisecond_timings()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse("""{"voice_id":"voice-123","name":"Test voice"}""")
            : TimestampedResponse());
        var provider = new ElevenLabsSpeechClipProvider(Client(handler));
        var spec = SpeechClipSpec.ForTimestamped("voice-123", 0.4f, 0.6f, 0.9f, "가나");

        var clip = await provider.GenerateTimestampedAsync(spec, CancellationToken.None);

        handler.Requests.Select(r => r.Path).Should().Equal(
            "/v1/voices/voice-123",
            "/v1/text-to-speech/voice-123/with-timestamps");
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.GetProperty("model_id").GetString().Should().Be("eleven_multilingual_v2");
        var settings = body.RootElement.GetProperty("voice_settings");
        settings.GetProperty("stability").GetSingle().Should().Be(0.4f);
        settings.GetProperty("similarity_boost").GetSingle().Should().Be(0.6f);
        settings.GetProperty("speed").GetSingle().Should().Be(0.9f);
        settings.GetProperty("style").GetSingle().Should().Be(0.45f);

        clip.Audio.Should().Equal(9, 8, 7);
        clip.Characters.Select(c => c.Char).Should().Equal("가", "나");
        clip.Characters[1].StartMs.Should().BeApproximately(250, 0.001);
        clip.Characters[1].EndMs.Should().BeApproximately(500, 0.001);
        clip.DurationSeconds.Should().BeApproximately(0.5, 0.0001);
    }

    private static ElevenLabsClient Client(HttpMessageHandler handler) =>
        new("test-key-not-real", httpClient: new HttpClient(handler));

    private static HttpResponseMessage AudioResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.Add("history-item-id", "history-1");
        return response;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TimestampedResponse()
    {
        var json = JsonSerializer.Serialize(new
        {
            audio_base64 = Convert.ToBase64String([9, 8, 7]),
            alignment = new
            {
                characters = new[] { "가", "나" },
                character_start_times_seconds = new[] { 0.0, 0.25 },
                character_end_times_seconds = new[] { 0.25, 0.5 }
            }
        });
        var response = JsonResponse(json);
        response.Headers.Add("history-item-id", "history-2");
        return response;
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Query, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, body));
            return respond(request);
        }
    }
}
