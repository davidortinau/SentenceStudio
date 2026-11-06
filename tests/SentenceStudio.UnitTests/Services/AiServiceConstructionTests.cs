using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// AiService's two construction paths. Client hosts (MAUI, the native heads) route through the
/// API gateways and need no OpenAI key and no chat client; server hosts call Foundry directly
/// and need the key only on the direct speech path. Nothing direct-only is read or built at
/// construction.
/// </summary>
public class AiServiceConstructionTests
{
    [Fact]
    public async Task GatewayHost_ConstructsWithoutKeyOrChatClient_AndRoutesThroughTheGateways()
    {
        var http = new CountingHttpClientFactory();
        var ai = new FakeAiGateway("from the gateway");
        var speech = new FakeSpeechGateway();
        using var provider = Services(new ConfigurationBuilder().Build(), http, services =>
        {
            services.AddSingleton<IAiGatewayClient>(ai);
            services.AddSingleton<ISpeechGatewayClient>(speech);
        });

        // No Settings section, no IChatClient registration: DI still builds it.
        var service = provider.GetRequiredService<AiService>();
        http.Created.Should().BeEmpty("no audio client or HttpClient is built at construction");

        (await service.SendPrompt<string>("prompt", AiTier.Reasoning)).Should().Be("from the gateway");
        ai.Tiers.Should().Equal(AiTier.Reasoning);
        await using var audio = await service.TextToSpeechAsync("안녕하세요", "voice");
        audio.Should().BeSameAs(speech.Audio);
        http.Created.Should().BeEmpty("the gateway paths never build the direct Foundry client");
    }

    [Fact]
    public async Task DirectHost_UsesItsChatClient_AndConstructsWithoutTheKey()
    {
        var http = new CountingHttpClientFactory();
        var chat = new FakeChatClient("{\"Text\":\"from Foundry\"}");
        using var provider = Services(new ConfigurationBuilder().Build(), http,
            services => services.AddSingleton<IChatClient>(chat));

        var service = provider.GetRequiredService<AiService>();
        http.Created.Should().BeEmpty();

        var answer = await service.SendPrompt<Answer>("prompt");
        answer.Text.Should().Be("from Foundry");
        chat.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DirectSpeech_RequiresTheKey_OnlyWhenItRuns()
    {
        var http = new CountingHttpClientFactory();
        using var provider = Services(new ConfigurationBuilder().Build(), http, _ => { });
        var service = provider.GetRequiredService<AiService>();

        var speak = () => service.TextToSpeechAsync("text", "voice");

        (await speak.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Settings:OpenAIKey*");
        http.Created.Should().BeEmpty("the key is checked before any client is built");
    }

    [Fact]
    public async Task DirectSpeech_BuildsTheFoundryAudioClientOnFirstUse()
    {
        var handler = new SpeechHandler();
        var http = new CountingHttpClientFactory(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:OpenAIKey"] = "fixture-only-direct-key",
                ["AI:OpenAI:Endpoint"] = "https://foundry.example.test/openai/v1",
            })
            .Build();
        using var provider = Services(configuration, http, _ => { });
        var service = provider.GetRequiredService<AiService>();
        http.Created.Should().BeEmpty();

        await using var audio = await service.TextToSpeechAsync("text", "voice");

        http.Created.Should().Equal("openai");
        handler.Paths.Should().ContainSingle().Which.Should().EndWith("/audio/speech");
        audio.Should().NotBeNull();
    }

    [Fact]
    public async Task GatewayHostWithoutAGatewayOrChatClient_FailsClosed()
    {
        using var provider = Services(new ConfigurationBuilder().Build(),
            new CountingHttpClientFactory(), _ => { });
        var service = provider.GetRequiredService<AiService>();

        var send = () => service.SendPrompt<string>("prompt");

        await send.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ServiceProvider Services(IConfiguration configuration,
        CountingHttpClientFactory http, Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton<IHttpClientFactory>(http);
        services.AddSingleton<IConnectivityService>(new Online());
        configure(services);
        services.AddSingleton<AiService>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    public sealed record Answer(string Text);

    private sealed class Online : IConnectivityService
    {
        public bool IsInternetAvailable => true;
    }

    private sealed class CountingHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public List<string> Created { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Created.Add(name);
            return handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class FakeAiGateway(string answer) : IAiGatewayClient
    {
        public List<AiTier> Tiers { get; } = [];

        public Task<T?> SendPromptAsync<T>(string prompt, AiTier tier = AiTier.Fast,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            Tiers.Add(tier);
            return Task.FromResult((T?)(object)answer);
        }

        public Task<T?> SendMessagesAsync<T>(List<(string role, string content)> messages,
            string? instructions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> AnalyzeImageAsync(string prompt, string imageBase64,
            string mediaType = "image/jpeg", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSpeechGateway : ISpeechGatewayClient
    {
        public MemoryStream Audio { get; } = new([1, 2, 3]);

        public Task<Stream?> SynthesizeAsync(string text, string voice, float speed = 1.0f,
            CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(Audio);
    }

    private sealed class FakeChatClient(string json) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class SpeechHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("RIFF0000WAVE"))
            });
        }
    }
}
