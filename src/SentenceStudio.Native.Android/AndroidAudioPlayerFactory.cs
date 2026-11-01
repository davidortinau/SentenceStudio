using Android.Content;
using Android.Media;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Non-UI MediaPlayer playback for the shared TimestampedAudioManager
/// (Reading read-along). The clip is written to app-private cache because
/// MediaPlayer reads from a file descriptor; the file is deleted on dispose.
/// </summary>
internal sealed class AndroidAudioPlayerFactory(Context context) : IAudioPlayerFactory
{
    private const int MaxAudioBytes = 32 * 1024 * 1024;

    public bool CanPlayLocally => true;

    public IUiAudioPlayer? CreatePlayer(System.IO.Stream audioStream)
    {
        ArgumentNullException.ThrowIfNull(audioStream);
        var directory = Path.Combine(context.CacheDir!.AbsolutePath, "reading-audio");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.mp3");
        using (var file = File.Create(path))
            audioStream.CopyTo(file);
        var length = new FileInfo(path).Length;
        if (length is 0 or > MaxAudioBytes)
        {
            File.Delete(path);
            return null;
        }
        var player = new MediaPlayer();
        try
        {
            player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!);
            player.SetDataSource(path);
            player.Prepare();
            return new Player(player, path);
        }
        catch (Exception)
        {
            player.Release();
            File.Delete(path);
            return null;
        }
    }

    private sealed class Player : Java.Lang.Object, IUiAudioPlayer, MediaPlayer.IOnCompletionListener
    {
        private readonly MediaPlayer _player;
        private readonly string _path;
        private bool _disposed;

        public Player(MediaPlayer player, string path)
        {
            _player = player;
            _path = path;
            _player.SetOnCompletionListener(this);
        }

        public event EventHandler? PlaybackEnded;

        public bool IsPlaying => !_disposed && _player.IsPlaying;

        public double CurrentPosition => _disposed ? 0 : _player.CurrentPosition / 1000.0;

        public double Duration => _disposed ? 0 : _player.Duration / 1000.0;

        public void OnCompletion(MediaPlayer? mp) => PlaybackEnded?.Invoke(this, EventArgs.Empty);

        public void Play()
        {
            if (!_disposed)
                _player.Start();
        }

        public void Pause()
        {
            if (!_disposed && _player.IsPlaying)
                _player.Pause();
        }

        public void Stop()
        {
            if (_disposed)
                return;
            if (_player.IsPlaying)
                _player.Pause();
            _player.SeekTo(0);
        }

        public void Seek(double seconds)
        {
            if (!_disposed)
                _player.SeekTo((int)Math.Clamp(seconds * 1000, 0, _player.Duration));
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _player.SetOnCompletionListener(null);
                _player.Release();
                try
                {
                    File.Delete(_path);
                }
                catch (IOException)
                {
                }
            }
            base.Dispose(disposing);
        }
    }
}
