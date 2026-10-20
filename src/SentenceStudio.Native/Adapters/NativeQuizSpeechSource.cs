using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Adapters;

public interface INativeQuizSpeechSource
{
    Task<Stream?> SynthesizeAsync(
        string owner, string promptText, string voiceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NativeQuizVoice>> GetVoicesAsync(
        string owner, string language, CancellationToken cancellationToken);
}

public sealed record NativeQuizVoice(string Id, string Name, string Language);

internal sealed record NativeQuizVoiceResponse(IReadOnlyList<NativeQuizVoice> Voices);

public sealed class NativeQuizSpeechSource(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IHttpMessageHandlerFactory handlers,
    ILogger<SpeechGatewayClient> logger,
    Uri apiRoot) : INativeQuizSpeechSource
{
    public async Task<Stream?> SynthesizeAsync(
        string owner, string promptText, string voiceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = scope.CaptureStamp();
        bool HasOwner() =>
            !string.IsNullOrWhiteSpace(owner)
            && string.Equals(stamp.PublishedOwner, owner, StringComparison.Ordinal)
            && scope.IsCurrent(stamp)
            && scope.TryGetUserProfileId(out var active)
            && string.Equals(active, owner, StringComparison.Ordinal)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal)
            && auth.IsSignedIn;

        if (!HasOwner() || string.IsNullOrWhiteSpace(promptText)
            || string.IsNullOrWhiteSpace(voiceId))
            throw new UnauthorizedAccessException("Quiz speech needs an authenticated owner and a prompt.");

        using var client = CreateClient(owner, HasOwner);
        var gateway = new SpeechGatewayClient(new SpeechApiClient(client), logger);
        var audio = await gateway.SynthesizeAsync(
            promptText, voiceId, cancellationToken: cancellationToken);
        if (!HasOwner())
        {
            audio?.Dispose();
            throw new UnauthorizedAccessException("Quiz speech owner changed during synthesis.");
        }
        return audio;
    }

    public async Task<IReadOnlyList<NativeQuizVoice>> GetVoicesAsync(
        string owner, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (language is not ("Korean" or "English" or "French" or "German" or "Spanish"))
            return [];
        var stamp = scope.CaptureStamp();
        bool HasOwner() =>
            !string.IsNullOrWhiteSpace(owner)
            && stamp.PublishedOwner == owner && scope.IsCurrent(stamp)
            && scope.TryGetUserProfileId(out var active) && active == owner
            && preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) == owner
            && auth.IsSignedIn;
        if (!HasOwner())
            throw new UnauthorizedAccessException("Voice discovery requires the active owner.");

        using var client = CreateClient(owner, HasOwner);
        var response = await client.GetFromJsonAsync<NativeQuizVoiceResponse>(
            "/api/v1/speech/voices?language=" + language, cancellationToken);
        if (!HasOwner())
            throw new UnauthorizedAccessException("Voice discovery owner changed.");
        return response?.Voices ?? [];
    }

    private HttpClient CreateClient(string owner, Func<bool> hasOwner)
    {
        var bearer = new OwnerBoundNativeAiBearerHandler(auth, apiRoot, owner, hasOwner)
        {
            InnerHandler = handlers.CreateHandler(OwnerBoundNativeAiGateway.ClientName)
        };
        // The handler factory owns the pooled inner pipeline.
        return new HttpClient(bearer, disposeHandler: false)
        {
            BaseAddress = apiRoot,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}

public static class NativeQuizSpeechRegistration
{
    public static IServiceCollection AddOwnerBoundNativeQuizSpeech(
        this IServiceCollection services, Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!apiRoot.IsAbsoluteUri
            || apiRoot.Scheme is not ("http" or "https"))
            throw new ArgumentException("Speech needs the configured API origin.", nameof(apiRoot));

        services.TryAddSingleton<INativeQuizSpeechSource>(sp =>
            new NativeQuizSpeechSource(
                sp.GetRequiredService<IAuthService>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<DeviceUserScopeProvider>(),
                sp.GetRequiredService<IHttpMessageHandlerFactory>(),
                sp.GetRequiredService<ILogger<SpeechGatewayClient>>(),
                apiRoot));
        return services;
    }
}
