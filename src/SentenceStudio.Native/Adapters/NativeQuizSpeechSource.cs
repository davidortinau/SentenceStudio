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
    Uri apiRoot,
    TimeSpan? timeout = null,
    NativeServerReachability? reachability = null) : INativeQuizSpeechSource
{
    /// <summary>Bounded wait for synthesized prompt audio or the voice list.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(10);

    private TimeSpan SpeechTimeout => timeout ?? DefaultTimeout;

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

        FailFastWhenRecentlyUnreachable();
        using var client = CreateClient(owner, HasOwner);
        var gateway = new SpeechGatewayClient(new SpeechApiClient(client), logger);
        var audio = await ObserveAsync(() => gateway.SynthesizeAsync(
            promptText, voiceId, cancellationToken: cancellationToken), cancellationToken);
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

        FailFastWhenRecentlyUnreachable();
        using var client = CreateClient(owner, HasOwner);
        var response = await ObserveAsync(() => client.GetFromJsonAsync<NativeQuizVoiceResponse>(
            "/api/v1/speech/voices?language=" + language, cancellationToken), cancellationToken);
        if (!HasOwner())
            throw new UnauthorizedAccessException("Voice discovery owner changed.");
        return response?.Voices ?? [];
    }

    /// <summary>
    /// Right after the server failed to answer (for example the pre-quiz sync timed out),
    /// take the existing no-audio / connection-error fallback now rather than after
    /// another full speech timeout.
    /// </summary>
    private void FailFastWhenRecentlyUnreachable()
    {
        if (reachability?.IsRecentlyUnreachable == true)
            throw new HttpRequestException(
                "Speech skipped: the server did not answer a recent request.",
                new TimeoutException());
    }

    private async Task<T> ObserveAsync<T>(Func<Task<T>> send, CancellationToken cancellationToken)
    {
        try
        {
            var result = await send();
            reachability?.RecordResponse();
            return result;
        }
        catch (HttpRequestException ex)
        {
            // A status code is an answer; only a connection failure opens the window.
            if (ex.StatusCode is null)
                reachability?.RecordFailure();
            else
                reachability?.RecordResponse();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient timeout elapsed: the server did not answer in time.
            reachability?.RecordFailure();
            throw;
        }
    }

    private HttpClient CreateClient(string owner, Func<bool> hasOwner)
    {
        var bearer = new OwnerBoundNativeAiBearerHandler(auth, apiRoot, owner, hasOwner)
        {
            InnerHandler = handlers.CreateHandler(OwnerBoundNativeAiGateway.ClientName)
        };
        // The handler factory owns the pooled inner pipeline. The quiz waits for autoplayed
        // prompt audio before presenting each question, so an unanswered request must fall
        // back to the existing no-audio state quickly rather than after 30 seconds.
        return new HttpClient(bearer, disposeHandler: false)
        {
            BaseAddress = apiRoot,
            Timeout = SpeechTimeout
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
                apiRoot,
                reachability: sp.GetRequiredService<NativeServerReachability>()));
        services.TryAddSingleton<NativeServerReachability>();
        return services;
    }
}
