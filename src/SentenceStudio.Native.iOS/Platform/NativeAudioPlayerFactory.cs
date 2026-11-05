using AVFoundation;
using Foundation;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Non-UI AVAudioPlayer playback for the shared TimestampedAudioManager
/// (Reading read-along). Seek and position use AVAudioPlayer.CurrentTime.
/// </summary>
public sealed class NativeAudioPlayerFactory : IAudioPlayerFactory
{
    private const int MaxAudioBytes = 32 * 1024 * 1024;

    public bool CanPlayLocally => true;

    public IUiAudioPlayer? CreatePlayer(Stream audioStream)
    {
        ArgumentNullException.ThrowIfNull(audioStream);
        using var buffer = new MemoryStream();
        audioStream.CopyTo(buffer);
        if (buffer.Length is 0 or > MaxAudioBytes)
            return null;
        var data = NSData.FromArray(buffer.ToArray());
        var player = AVAudioPlayer.FromData(data, out var error);
        if (player is null || error is not null)
        {
            player?.Dispose();
            data.Dispose();
            return null;
        }
        player.PrepareToPlay();
        return new Player(player, data);
    }

    private sealed class Player : IUiAudioPlayer
    {
        private readonly AVAudioPlayer _player;
        private readonly NSData _data;
        private readonly EventHandler<AVStatusEventArgs> _finished;
        private bool _disposed;

        public Player(AVAudioPlayer player, NSData data)
        {
            _player = player;
            _data = data;
            _finished = (_, _) => PlaybackEnded?.Invoke(this, EventArgs.Empty);
            _player.FinishedPlaying += _finished;
        }

        public event EventHandler? PlaybackEnded;

        public bool IsPlaying => !_disposed && _player.Playing;

        public double CurrentPosition => _disposed ? 0 : _player.CurrentTime;

        public double Duration => _disposed ? 0 : _player.Duration;

        public void Play()
        {
            if (_disposed)
                return;
            NativeAudioSession.UsePlayback();
            _player.Play();
        }

        public void Pause()
        {
            if (!_disposed)
                _player.Pause();
        }

        public void Stop()
        {
            if (_disposed)
                return;
            _player.Stop();
            _player.CurrentTime = 0;
        }

        public void Seek(double seconds)
        {
            if (!_disposed)
                _player.CurrentTime = Math.Clamp(seconds, 0, _player.Duration);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _player.FinishedPlaying -= _finished;
            _player.Stop();
            _player.Dispose();
            _data.Dispose();
        }
    }
}
