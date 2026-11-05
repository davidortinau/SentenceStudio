using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Speech;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Native Reading audio is for the server's copy of the transcript: a clip for other text is
/// neither played nor kept, and a clip in the API's default voice is played but not kept.
/// </summary>
public sealed class NativeReadingAudioSourceTests
{
    private const string Transcript = "가나다";

    [Theory]
    [InlineData(true, false, true, true)]    // same transcript, requested voice: played and kept
    [InlineData(false, false, false, false)] // the server read other text: neither
    [InlineData(true, true, true, false)]    // the API's default voice: played, not kept
    public async Task Reading_audio_is_used_and_kept_only_for_this_transcript_and_voice(
        bool sameTranscript, bool substituted, bool played, bool kept)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password");
        var server = new TimestampedServer
        {
            TranscriptSha256 = TimestampedSpeech.TranscriptSha256(sameTranscript ? Transcript : Transcript + " (edited)"),
            Substituted = substituted
        };
        var transport = new ServiceCollection();
        transport.AddHttpClient(OwnerBoundNativeAiGateway.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => server);
        using var provider = transport.BuildServiceProvider();
        var files = fixture.Provider.GetRequiredService<IFileSystemService>();
        var source = new NativeReadingAudioSource(
            fixture.Provider.GetRequiredService<IAuthService>(),
            fixture.Preferences, fixture.UserScope,
            provider.GetRequiredService<IHttpMessageHandlerFactory>(),
            files, NullLogger<NativeReadingAudioSource>.Instance,
            new Uri("https://api.example.invalid/"));
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = NativeSliceFixture.UserA,
            Title = "Reading",
            Transcript = Transcript
        };

        var audio = await source.GetAsync(NativeSliceFixture.UserA, resource, default);

        Assert.Equal(played, audio is not null);
        if (played)
            Assert.Equal([1, 2, 3, 4], audio!.AudioData);
        var cachedFiles = Directory.Exists(files.AppDataDirectory)
            ? Directory.GetFiles(files.AppDataDirectory, $"timestamped_{resource.Id}_*")
            : [];
        Assert.Equal(kept, cachedFiles.Length == 2);
        if (!kept)
            Assert.Empty(cachedFiles);
        // Task-owned scratch: the fixture removes only its own database files.
        foreach (var file in cachedFiles)
            File.Delete(file);
    }

    private sealed class TimestampedServer : HttpMessageHandler
    {
        public string? TranscriptSha256 { get; init; }
        public bool Substituted { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SynthesizeTimestampedResponse
                {
                    AudioUrl = "data:audio/mpeg;base64,AQIDBA==",
                    DurationSeconds = 0.3,
                    Characters = Transcript.Select((c, i) => new CharacterTimestamp
                    {
                        Char = c.ToString(), StartMs = i * 100, EndMs = (i + 1) * 100
                    }).ToList(),
                    VoiceId = Substituted ? "21m00Tcm4TlvDq8ikWAM" : "AW5wrnG1jVizOYY7R1Oo",
                    VoiceSubstituted = Substituted,
                    TranscriptSha256 = TranscriptSha256
                })
            });
    }
}
