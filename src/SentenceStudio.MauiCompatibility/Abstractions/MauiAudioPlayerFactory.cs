using Plugin.Maui.Audio;

namespace SentenceStudio.Abstractions;

public sealed class MauiAudioPlayerFactory : IAudioPlayerFactory
{
    public bool CanPlayLocally => true;

    public IUiAudioPlayer? CreatePlayer(Stream audioStream)
    {
        ArgumentNullException.ThrowIfNull(audioStream);

        // Callers may dispose their stream as soon as the player is created.
        var ownedStream = new MemoryStream();
        try
        {
            audioStream.CopyTo(ownedStream);
            ownedStream.Position = 0;
            var player = AudioManager.Current.CreatePlayer(ownedStream);
            if (player is null)
            {
                ownedStream.Dispose();
                return null;
            }

            return new MauiUiAudioPlayer(player, ownedStream);
        }
        catch
        {
            ownedStream.Dispose();
            throw;
        }
    }

    private sealed class MauiUiAudioPlayer(IAudioPlayer player, Stream ownedStream) : IUiAudioPlayer
    {
        public event EventHandler? PlaybackEnded
        {
            add => player.PlaybackEnded += value;
            remove => player.PlaybackEnded -= value;
        }

        public bool IsPlaying => player.IsPlaying;
        public double CurrentPosition => player.CurrentPosition;
        public double Duration => player.Duration;

        public void Play() => player.Play();
        public void Pause() => player.Pause();
        public void Stop() => player.Stop();
        public void Seek(double seconds) => player.Seek(seconds);

        public void Dispose()
        {
            player.Dispose();
            ownedStream.Dispose();
        }
    }
}
