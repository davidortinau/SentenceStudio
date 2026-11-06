using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The shared AiService on a native head (as MAUI runs it): no OpenAI key and no direct chat
/// client on the device; prompts and speech go through the owner-bound API gateways as the
/// current device owner, and an absent or inconsistent owner sends nothing.
/// </summary>
public sealed class NativeSharedAiTests
{
    private const string A = NativeSliceFixture.UserA;
    private const string B = NativeSliceFixture.UserB;

    [Fact]
    public async Task NativeRegistration_RunsTheSharedServicesWithoutAKey_AsTheCurrentOwner()
    {
        var preferences = new NativeSliceFixture.ScratchPreferences();
        var scope = new DeviceUserScopeProvider();
        var gateway = new ScratchOwnerAi { Polished = "polished text" };
        var speech = new ScratchSpeech();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPreferencesService>(preferences);
        services.AddSingleton(scope);
        services.AddSingleton<IConnectivityService>(new Online());
        services.AddSingleton<IOwnerBoundNativeAiGateway>(gateway);
        services.AddSingleton<INativeQuizSpeechSource>(speech);
        services.AddNativeSharedAi();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        // No Settings section, no key, no direct chat client: the shared services resolve.
        Assert.IsType<NativeOwnerAiGatewayClient>(provider.GetRequiredService<IAiGatewayClient>());
        Assert.IsType<NativeOwnerSpeechGatewayClient>(provider.GetRequiredService<ISpeechGatewayClient>());
        var ai = provider.GetRequiredService<AiService>();
        var formatting = provider.GetRequiredService<TranscriptFormattingService>();
        Assert.IsType<YouTubeImportService>(provider.GetRequiredService<IYouTubeImportService>());

        Activate(scope, preferences, A);
        var polished = await formatting.PolishWithAiAsync("안녕하세요\n반갑습니다", "ko");
        Assert.Equal("polished text", polished);
        Assert.Equal([A], gateway.Owners);
        Assert.Equal([AiTier.Fast], gateway.Tiers);
        // The source's CleanTranscript prompt, embedded in the native assembly, carries the text.
        Assert.Contains("안녕하세요", gateway.Prompts.Single());

        await using var audio = await ai.TextToSpeechAsync("안녕", "voice-1");
        Assert.Same(speech.Audio, audio);
        Assert.Equal([A], speech.Owners);

        // An absent owner sends nothing (the polish fails; no request leaves the device).
        scope.SetActiveUser(null);
        await Assert.ThrowsAnyAsync<Exception>(() => formatting.PolishWithAiAsync("다른 글", "ko"));
        Assert.Single(gateway.Prompts);
        // A profile pointer that names another owner is refused too.
        Activate(scope, preferences, A);
        preferences.Set(UserProfileRepository.ActiveProfileIdKey, B);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ai.SendPrompt<string>("x"));
        Assert.Single(gateway.Prompts);
    }

    private static void Activate(DeviceUserScopeProvider scope,
        NativeSliceFixture.ScratchPreferences preferences, string owner)
    {
        preferences.Set(UserProfileRepository.ActiveProfileIdKey, owner);
        scope.SetActiveUser(owner);
    }

    internal sealed class Online : IConnectivityService
    {
        public bool IsInternetAvailable => true;
    }

    internal sealed class ScratchOwnerAi : IOwnerBoundNativeAiGateway
    {
        public string? Polished { get; set; }
        public Exception? Failure { get; set; }
        public List<string> Owners { get; } = [];
        public List<AiTier> Tiers { get; } = [];
        public List<string> Prompts { get; } = [];

        public Task<T?> SendPromptAsync<T>(string owner, string prompt, CancellationToken cancellationToken) =>
            SendPromptAsync<T>(owner, prompt, AiTier.Fast, cancellationToken);

        public Task<T?> SendPromptAsync<T>(string owner, string prompt, AiTier tier,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
                throw Failure;
            Owners.Add(owner);
            Tiers.Add(tier);
            Prompts.Add(prompt);
            return Task.FromResult((T?)(object?)Polished);
        }
    }

    private sealed class ScratchSpeech : INativeQuizSpeechSource
    {
        public MemoryStream Audio { get; } = new([1, 2, 3]);
        public List<string> Owners { get; } = [];

        public Task<Stream?> SynthesizeAsync(string owner, string promptText, string voiceId,
            CancellationToken cancellationToken)
        {
            Owners.Add(owner);
            return Task.FromResult<Stream?>(Audio);
        }

        public Task<IReadOnlyList<NativeQuizVoice>> GetVoicesAsync(string owner, string language,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
