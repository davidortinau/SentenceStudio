using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizSpeechSourceTests
{
    [Fact]
    public async Task QuizSpeechAndVoices_UseOwnerBearerAndExistingApiWithoutClientSecret()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var signedIn = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativeSliceFixture.UserA, signedIn.UserId);
        var server = new ScratchSpeechHandler();
        var transport = new ServiceCollection();
        transport.AddHttpClient(OwnerBoundNativeAiGateway.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => server);
        using var provider = transport.BuildServiceProvider();
        var source = new NativeQuizSpeechSource(
            fixture.Provider.GetRequiredService<IAuthService>(),
            fixture.Preferences, fixture.UserScope,
            provider.GetRequiredService<IHttpMessageHandlerFactory>(),
            NullLogger<SpeechGatewayClient>.Instance,
            new Uri("https://api.example.invalid/"));

        using var audio = await source.SynthesizeAsync(
            NativeSliceFixture.UserA, "target-language phrase", "voice-1", default);
        Assert.NotNull(audio);
        var bytes = new byte[4];
        Assert.Equal(4, await audio.ReadAsync(bytes));
        Assert.Equal([1, 2, 3, 4], bytes);
        var voices = await source.GetVoicesAsync(
            NativeSliceFixture.UserA, "Korean", default);
        Assert.Equal("voice-1", Assert.Single(voices).Id);
        Assert.Equal(2, server.RequestPaths.Count);
        Assert.All(server.Bearers, bearer => Assert.False(string.IsNullOrWhiteSpace(bearer)));
        Assert.Contains("/api/v1/speech/synthesize", server.RequestPaths);
        Assert.Contains("/api/v1/speech/voices?language=Korean", server.RequestPaths);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            source.GetVoicesAsync(NativeSliceFixture.UserB, "Korean", default));
        fixture.Activate(NativeSliceFixture.UserB);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            source.SynthesizeAsync(
                NativeSliceFixture.UserA, "must not leave client", "voice-1", default));
        Assert.Equal(2, server.RequestPaths.Count);
    }

    private sealed class ScratchSpeechHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];
        public List<string?> Bearers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.PathAndQuery);
            Bearers.Add(request.Headers.Authorization?.Parameter);
            var response = request.RequestUri.AbsolutePath.EndsWith(
                "/voices", StringComparison.Ordinal)
                ? new { voices = new[] { new {
                    id = "voice-1", name = "Source Voice", language = "Korean" } } }
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = response is not null
                    ? JsonContent.Create(response)
                    : JsonContent.Create(new {
                        audioUrl = "data:audio/mpeg;base64,AQIDBA==" })
            });
        }
    }
}
