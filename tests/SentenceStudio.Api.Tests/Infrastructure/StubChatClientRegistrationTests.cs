using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Tests.Coach;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// Guards the no-network <see cref="IChatClient"/> stub that lets the API test host boot with no
/// AI configuration.
///
/// The stub fixed 59 host-boot failures across the auth, profile, speech, and plan-tracking
/// suites. The danger with a fix like that is silent over-reach: if it also landed in hosts that
/// deliberately install their own chat client, those tests would start asserting against the stub
/// instead of their fake and would keep passing while proving nothing. These tests pin the
/// boundary from both directions.
/// </summary>
public class StubChatClientRegistrationTests
{
    // -----------------------------------------------------------------
    // Registration semantics
    // -----------------------------------------------------------------

    [Fact]
    public void Stub_IsRegistered_WhenNoAiEndpointIsConfigured()
    {
        var services = new ServiceCollection();

        TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, EmptyConfiguration());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IChatClient>().Should().BeOfType<UnconfiguredAiChatClient>();
    }

    [Fact]
    public void Stub_IsNotRegistered_WhenAnAiEndpointIsConfigured()
    {
        var services = new ServiceCollection();

        TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(
            services,
            ConfigurationWith(TestApiHostConfigurator.AiEndpointConfigKey, "https://example.invalid/openai/v1"));

        using var provider = services.BuildServiceProvider();
        provider.GetService<IChatClient>().Should().BeNull(
            "with an endpoint configured the host gets whatever Program.cs registers; the stub " +
            "must not shadow the real tiered client registration");
    }

    [Fact]
    public void Stub_DoesNotReplace_AClientTheFactoryAlreadyRegistered()
    {
        // The masking scenario in miniature: a factory installs its own fake first.
        var services = new ServiceCollection();
        var deliberateFake = new RecordingChatClient();
        services.AddSingleton<IChatClient>(deliberateFake);

        TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, EmptyConfiguration());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IChatClient>().Should().BeSameAs(deliberateFake,
            "TryAdd semantics — a deliberately registered client always wins over the stub");
    }

    [Fact]
    public void Stub_RegistersExactlyOneChatClient()
    {
        var services = new ServiceCollection();

        TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, EmptyConfiguration());
        TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, EmptyConfiguration());

        services.Count(d => d.ServiceType == typeof(IChatClient)).Should().Be(1,
            "a second call must be idempotent, not stack another registration");
    }

    // -----------------------------------------------------------------
    // The stub is inert, not functional
    // -----------------------------------------------------------------

    [Fact]
    public async Task Stub_ThrowsIfATestActuallyCallsTheModel()
    {
        var stub = new UnconfiguredAiChatClient();

        var act = async () => await stub.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a stub that returned canned content would let an AI-dependent test pass without " +
            "proving anything — the whole point is that it cannot silently satisfy an assertion");
        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Stub_ThrowsOnStreamingToo()
    {
        var stub = new UnconfiguredAiChatClient();

        var act = async () =>
        {
            await foreach (var _ in stub.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // -----------------------------------------------------------------
    // End-to-end: real hosts, real boot
    // -----------------------------------------------------------------

    [Fact]
    public void CoachHost_KeepsItsOwnRecordingClient_AndNeverSeesTheStub()
    {
        // The load-bearing assertion. CoachApiFactory boots with no AI configuration and installs
        // RecordingChatClient so its "the coach never calls the model" tests mean something. If the
        // stub ever leaked into that host, those assertions would silently go hollow.
        using var factory = new CoachApiFactory();

        var resolved = factory.Services.GetRequiredService<IChatClient>();

        resolved.Should().BeSameAs(factory.ChatClient);
        resolved.Should().BeOfType<RecordingChatClient>();
        resolved.Should().NotBeOfType<UnconfiguredAiChatClient>();
        factory.ChatClient.CallCount.Should().Be(0, "booting the host must not call the model");
    }

    [Theory]
    [InlineData(typeof(DevAuthApiFactory))]
    [InlineData(typeof(JwtBearerApiFactory))]
    [InlineData(typeof(ProfileSpeechApiFactory))]
    public void UnconfiguredHosts_BootAndResolveTheStub(Type factoryType)
    {
        // These three set AI:OpenAI:ApiKey but no AI:OpenAI:Endpoint, and Program.cs gates the
        // IChatClient registration on the endpoint — so without the stub, AiService fails DI
        // validation and the host never starts.
        using var factory = (IDisposable)Activator.CreateInstance(factoryType)!;
        var services = ((IServiceProvider)factoryType.GetProperty("Services")!.GetValue(factory)!);

        services.GetRequiredService<IChatClient>().Should().BeOfType<UnconfiguredAiChatClient>();

        // Proves the boot path is genuinely exercised, not just the container.
        services.GetRequiredService<SentenceStudio.Services.AiService>().Should().NotBeNull();
    }

    private static IConfiguration EmptyConfiguration()
        => new ConfigurationBuilder().Build();

    private static IConfiguration ConfigurationWith(string key, string value)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
}
