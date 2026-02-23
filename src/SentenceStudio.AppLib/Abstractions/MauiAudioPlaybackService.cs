using Plugin.Maui.Audio;

namespace SentenceStudio.Abstractions;

public sealed class MauiAudioPlaybackService : IAudioPlaybackService
{
    private IAudioPlayer? _player;

    public Task PlayAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stop();

        _player = AudioManager.Current.CreatePlayer(audioStream);
        _player.Play();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
    }
}
