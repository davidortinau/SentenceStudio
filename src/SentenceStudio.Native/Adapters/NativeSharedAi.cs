using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.LanguageSegmentation;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// The shared <see cref="AiService"/> on a native head, as MAUI runs it: prompts go through
/// <see cref="IAiGatewayClient"/> and speech through <see cref="ISpeechGatewayClient"/>, that is,
/// through our API with the signed-in learner's token. The device holds no AI key and no
/// direct chat client; only the API holds the Foundry credentials. Each request is sent as the
/// current device owner through the existing owner-bound gateways, which refuse an absent or
/// changed owner before and after the call.
/// </summary>
public static class NativeSharedAiRegistration
{
    public static IServiceCollection AddNativeSharedAi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // AiService builds no HttpClient on the gateway path; the factory is only its dependency.
        services.AddHttpClient();
        services.TryAddSingleton<IAiGatewayClient, NativeOwnerAiGatewayClient>();
        services.TryAddSingleton<ISpeechGatewayClient, NativeOwnerSpeechGatewayClient>();
        services.TryAddSingleton(sp => new AiService(
            // No Settings or AI section: the gateway path reads none (model names are the
            // server's), and a direct path is unavailable here by design.
            new ConfigurationBuilder().Build(),
            sp.GetRequiredService<ILogger<AiService>>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp,
            sp.GetRequiredService<IAiGatewayClient>(),
            sp.GetRequiredService<ISpeechGatewayClient>()));

        // The shared YouTube services the source Media Import page runs on the device.
        services.TryAddSingleton<AudioAnalyzer>();
        services.TryAddSingleton<IYouTubeImportService, YouTubeImportService>();
        services.TryAddSingleton(sp => new TranscriptFormattingService(
            sp.GetRequiredService<AiService>(),
            // The segmenters CoreServiceExtensions registers, in its order.
            [
                new KoreanLanguageSegmenter(),
                new GenericLatinSegmenter(),
                new FrenchLanguageSegmenter(),
                new GermanLanguageSegmenter(),
                new SpanishLanguageSegmenter(),
            ],
            sp.GetRequiredService<ILogger<TranscriptFormattingService>>(),
            NativeTranscriptPrompts.Instance));
        return services;
    }
}

/// <summary>
/// <see cref="IAiGatewayClient"/> over <see cref="IOwnerBoundNativeAiGateway"/>: each prompt is
/// sent as the current device owner (the published owner, the profile pointer and the scope
/// agree), and the gateway re-checks that owner, the token's owner claim and the destination.
/// </summary>
public sealed class NativeOwnerAiGatewayClient(
    IServiceProvider services,
    DeviceUserScopeProvider scope,
    IPreferencesService preferences) : IAiGatewayClient
{
    public Task<T?> SendPromptAsync<T>(string prompt, AiTier tier = AiTier.Fast,
        string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        // The owner-bound gateway sends the tier's default effort only.
        if (reasoningEffort is not null)
            throw new NotSupportedException("Native AI prompts use the tier's default effort.");
        return Gateway().SendPromptAsync<T>(
            NativeCurrentOwner.Require(scope, preferences), prompt, tier, cancellationToken);
    }

    public Task<T?> SendMessagesAsync<T>(List<(string role, string content)> messages,
        string? instructions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        // AiService sends one user message with system instructions (SendPromptWithInstructions).
        if (messages is not [("user", var message)])
            throw new NotSupportedException("Native AI sends one user message with instructions.");
        return Gateway().SendWithInstructionsAsync<T>(
            NativeCurrentOwner.Require(scope, preferences), instructions ?? string.Empty, message,
            cancellationToken);
    }

    public Task<string> AnalyzeImageAsync(string prompt, string imageBase64,
        string mediaType = "image/jpeg", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Native image analysis does not go through AiService.");

    private IOwnerBoundNativeAiGateway Gateway() =>
        services.GetService<IOwnerBoundNativeAiGateway>()
        ?? throw new InvalidOperationException("The native API gateway is not configured.");
}

/// <summary>
/// <see cref="ISpeechGatewayClient"/> over the owner-bound <see cref="INativeQuizSpeechSource"/>
/// (/api/v1/speech/*) as the current device owner.
/// </summary>
public sealed class NativeOwnerSpeechGatewayClient(
    IServiceProvider services,
    DeviceUserScopeProvider scope,
    IPreferencesService preferences) : ISpeechGatewayClient
{
    public Task<Stream?> SynthesizeAsync(string text, string voice, float speed = 1.0f,
        CancellationToken cancellationToken = default)
    {
        // The owner-bound speech source synthesizes at the voice's normal speed only.
        if (speed != 1.0f)
            throw new NotSupportedException("Native speech synthesizes at normal speed.");
        var speech = services.GetService<INativeQuizSpeechSource>()
            ?? throw new InvalidOperationException("The native speech gateway is not configured.");
        return speech.SynthesizeAsync(
            NativeCurrentOwner.Require(scope, preferences), text, voice, cancellationToken);
    }
}

/// <summary>
/// The device owner a shared service's request is sent as: absent or inconsistent fails closed.
/// Inside <see cref="NativeOwnerCall.Bind"/> it must also be the bound owner (the page's), so a
/// device that changed hands mid-call sends nothing rather than the other owner's request.
/// </summary>
internal static class NativeCurrentOwner
{
    internal static string Require(DeviceUserScopeProvider scope, IPreferencesService preferences)
    {
        var stamp = scope.CaptureStamp();
        if (scope.TryGetUserProfileId(out var owner)
            && !string.IsNullOrWhiteSpace(owner)
            && string.Equals(stamp.PublishedOwner, owner, StringComparison.Ordinal)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal)
            && (NativeOwnerCall.Current is not { } bound
                || string.Equals(bound, owner, StringComparison.Ordinal)))
            return owner;
        throw new UnauthorizedAccessException("Native AI requires a current device owner.");
    }
}

/// <summary>
/// The owner a shared service's AI and speech requests belong to for the call in progress (a
/// page's owner). It flows with the async call into the shared service; outside a binding the
/// current device owner is used, as before.
/// </summary>
internal static class NativeOwnerCall
{
    private static readonly AsyncLocal<string?> Bound = new();

    internal static string? Current => Bound.Value;

    internal static IDisposable Bind(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var previous = Bound.Value;
        Bound.Value = owner;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Bound.Value = previous;
    }
}

/// <summary>The transcript prompt template (AppLib Resources/Raw), embedded in this assembly.</summary>
internal sealed class NativeTranscriptPrompts : IFileSystemService
{
    internal const string CleanTranscript = "CleanTranscript.scriban-txt";
    internal static readonly NativeTranscriptPrompts Instance = new();

    public string AppDataDirectory =>
        throw new NotSupportedException("Transcript formatting reads only its prompt template.");

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        if (filename != CleanTranscript)
            throw new FileNotFoundException("Transcript prompt is not embedded.", filename);
        var stream = typeof(NativeTranscriptPrompts).Assembly.GetManifestResourceStream(
                "SentenceStudio.Native.Prompts." + filename)
            ?? throw new FileNotFoundException("Transcript prompt is missing.", filename);
        return Task.FromResult(stream);
    }
}
