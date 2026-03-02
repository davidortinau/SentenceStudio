using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebAudioPlaybackService : IAudioPlaybackService
{
    public Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Server-side audio playback is not supported in SentenceStudio.WebApp.");
    }

    public void Stop()
    {
    }
}
