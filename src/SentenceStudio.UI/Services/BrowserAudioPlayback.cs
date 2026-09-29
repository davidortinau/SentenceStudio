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
        PlayCoreAsync(js, audioData, null, logger, cancellationToken);

    public static Task PlayAndTrackAsync<T>(
        IJSRuntime js,
        byte[] audioData,
        DotNetObjectReference<T> callbacks,
        CancellationToken cancellationToken = default,
        ILogger? logger = null) where T : class =>
        PlayCoreAsync(js, audioData, callbacks, logger, cancellationToken);

    private static async Task PlayCoreAsync(
        IJSRuntime js,
        byte[] audioData,
        object? callbacks,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(audioData);
        if (audioData.Length == 0)
            throw new InvalidOperationException("No audio was returned for browser playback.");
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var duration = await js.InvokeAsync<double>("audioInterop.loadFromBase64", cancellationToken,
                Convert.ToBase64String(audioData), "audio/mpeg");
            if (!double.IsFinite(duration) || duration <= 0)
                throw new InvalidOperationException("The browser could not load the audio.");

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
