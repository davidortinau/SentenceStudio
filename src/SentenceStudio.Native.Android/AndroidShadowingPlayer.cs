using Android.Media;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Non-UI MediaPlayer for one Shadowing clip. The clip is written to a
/// private cache file (MediaPlayer cannot read a byte array) that is deleted
/// on Stop. Slow/Normal is PlaybackParams speed with pitch preserved.
/// </summary>
internal sealed class AndroidShadowingPlayer(string cacheDirectory) : INativeShadowingPlayer
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private string? _file;
    private bool _playing;

    public event Action? Ended;

    public bool IsLoaded { get { lock (_gate) return _player is not null; } }

    public bool IsPlaying { get { lock (_gate) return _player is not null && _playing; } }

    public double DurationSeconds
    {
        get { lock (_gate) return _player is { } p ? Math.Max(0, p.Duration) / 1000.0 : 0; }
    }

    public double PositionSeconds
    {
        get { lock (_gate) return _player is { } p ? Math.Max(0, p.CurrentPosition) / 1000.0 : 0; }
    }

    public bool Load(byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        lock (_gate)
        {
            ReleaseCore();
            if (audio.Length == 0)
                return false;
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                _file = Path.Combine(cacheDirectory, $"shadow_play_{Guid.NewGuid():N}.mp3");
                File.WriteAllBytes(_file, audio);
                var player = new MediaPlayer();
                player.SetDataSource(_file);
                player.Prepare();
                player.Completion += OnCompletion;
                _player = player;
                return true;
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or IOException
                or UnauthorizedAccessException)
            {
                ReleaseCore();
                return false;
            }
        }
    }

    private void OnCompletion(object? sender, EventArgs e)
    {
        lock (_gate)
            _playing = false;
        Ended?.Invoke();
    }

    public bool Play(float rate)
    {
        lock (_gate)
        {
            if (_player is not { } player)
                return false;
            try
            {
                player.PlaybackParams = new PlaybackParams()
                    .SetSpeed(Math.Clamp(rate, 0.5f, 2f))
                    .SetPitch(1f);
                // Setting PlaybackParams on a prepared player starts it.
                if (!player.IsPlaying)
                    player.Start();
                _playing = true;
                return true;
            }
            catch (Java.Lang.Exception)
            {
                _playing = false;
                return false;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_player is { } player && player.IsPlaying)
                player.Pause();
            _playing = false;
        }
    }

    public void Seek(double fraction)
    {
        lock (_gate)
        {
            if (_player is { } player && double.IsFinite(fraction) && player.Duration > 0)
                player.SeekTo((int)(Math.Clamp(fraction, 0, 1) * player.Duration));
        }
    }

    public void Stop()
    {
        lock (_gate)
            ReleaseCore();
    }

    private void ReleaseCore()
    {
        _playing = false;
        if (_player is { } player)
        {
            player.Completion -= OnCompletion;
            try { player.Stop(); } catch (Java.Lang.IllegalStateException) { }
            player.Release();
            player.Dispose();
        }
        _player = null;
        if (_file is { } file)
        {
            // Task-owned temporary playback copy; the cached clip stays in AudioCache.
            try { File.Delete(file); } catch (IOException) { }
        }
        _file = null;
    }

    public void Dispose() => Stop();
}
