namespace SentenceStudio.Abstractions;

public interface IAudioPlaybackService
{
    Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default);
    void Stop();
}
