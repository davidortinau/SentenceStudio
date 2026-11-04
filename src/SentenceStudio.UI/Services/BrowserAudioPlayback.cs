using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SentenceStudio.WebUI.Services;

/// <summary>Uses the existing browser audio interop instead of trying to play on the server.</summary>
public static class BrowserAudioPlayback
{
    public static Task PlayAsync(
        IJSRuntime js,
        byte[] audioData,
        CancellationToken cancellationToken = default,
        ILogger? logger = null) =>
        PlayCoreAsync(js, audioData, null, null, logger, cancellationToken);

    /// <summary>
    /// Plays at <paramref name="playbackRate"/> times normal speed: the rate is
    /// applied to the loaded clip before playback starts.
    /// </summary>
    public static Task PlayAtRateAsync(
        IJSRuntime js,
        byte[] audioData,
        double playbackRate,
        CancellationToken cancellationToken = default,
        ILogger? logger = null) =>
        PlayCoreAsync(js, audioData, null, playbackRate, logger, cancellationToken);

    public static Task PlayAndTrackAsync<T>(
        IJSRuntime js,
        byte[] audioData,
        DotNetObjectReference<T> callbacks,
        CancellationToken cancellationToken = default,
        ILogger? logger = null) where T : class =>
        PlayCoreAsync(js, audioData, callbacks, null, logger, cancellationToken);

    private static async Task PlayCoreAsync(
        IJSRuntime js,
        byte[] audioData,
        object? callbacks,
        double? playbackRate,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(audioData);
        if (audioData.Length == 0)
            throw new InvalidOperationException("No audio was returned for browser playback.");
        if (playbackRate is { } requested && (!double.IsFinite(requested) || requested <= 0))
            throw new ArgumentOutOfRangeException(nameof(playbackRate));
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var duration = await js.InvokeAsync<double>("audioInterop.loadFromBase64", cancellationToken,
                Convert.ToBase64String(audioData), "audio/mpeg");
            if (!double.IsFinite(duration) || duration <= 0)
                throw new InvalidOperationException("The browser could not load the audio.");

            cancellationToken.ThrowIfCancellationRequested();
            if (playbackRate is { } rate
                && !await js.InvokeAsync<bool>("audioInterop.setPlaybackRate", cancellationToken, rate))
                throw new InvalidOperationException("The browser could not apply the playback speed.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!await js.InvokeAsync<bool>("audioInterop.startPlayback", cancellationToken, callbacks, 1000))
                throw new InvalidOperationException("The browser did not start audio playback.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception playbackError)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await js.InvokeVoidAsync("audioInterop.stop");
                }
                catch (Exception cleanupError)
                {
                    if (logger is null)
                        throw new AggregateException(
                            "Browser audio playback and cleanup both failed.", playbackError, cleanupError);

                    logger.LogWarning(cleanupError, "Failed to stop browser audio after a playback error");
                }
            }
            throw;
        }
    }
}
