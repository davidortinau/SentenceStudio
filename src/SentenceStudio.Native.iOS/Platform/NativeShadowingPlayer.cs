using AVFoundation;
using Foundation;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Non-UI AVAudioPlayer for one Shadowing clip. Slow/Normal is the player's
/// time-stretched rate (pitch preserved), because the synthesize API returns
/// one normal-speed clip per phrase and voice.
/// </summary>
public sealed class NativeShadowingPlayer : INativeShadowingPlayer
{
    private readonly object _gate = new();
    private AVAudioPlayer? _player;
    private NSData? _data;
    private EventHandler<AVStatusEventArgs>? _finished;

    public event Action? Ended;

    public bool IsLoaded
    {
        get { lock (_gate) return _player is not null; }
    }

    public bool IsPlaying
    {
        get { lock (_gate) return _player?.Playing == true; }
    }

    public double DurationSeconds
    {
        get { lock (_gate) return _player is { } p && double.IsFinite(p.Duration) ? p.Duration : 0; }
    }

    public double PositionSeconds
    {
        get { lock (_gate) return _player is { } p && double.IsFinite(p.CurrentTime) ? p.CurrentTime : 0; }
    }

    public bool Load(byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        lock (_gate)
        {
            ReleaseCore();
            if (audio.Length == 0)
                return false;
            _data = NSData.FromArray(audio);
            var player = AVAudioPlayer.FromData(_data, out var error);
            if (player is null || error is not null)
            {
                player?.Dispose();
                ReleaseCore();
                return false;
            }
            player.EnableRate = true;
            if (!player.PrepareToPlay())
            {
                player.Dispose();
                ReleaseCore();
                return false;
            }
            _finished = (_, _) => Ended?.Invoke();
            player.FinishedPlaying += _finished;
            _player = player;
            return true;
        }
    }

    public bool Play(float rate)
    {
        lock (_gate)
        {
            if (_player is not { } player)
                return false;
            player.Rate = Math.Clamp(rate, 0.5f, 2f);
            return player.Play();
        }
    }

    public void Pause()
    {
        lock (_gate)
            _player?.Pause();
    }

    public void Seek(double fraction)
    {
        lock (_gate)
        {
            if (_player is { } player && double.IsFinite(fraction) && player.Duration > 0)
                player.CurrentTime = Math.Clamp(fraction, 0, 1) * player.Duration;
        }
    }

    public void Stop()
    {
        lock (_gate)
            ReleaseCore();
    }

    private void ReleaseCore()
    {
        var player = _player;
        _player = null;
        if (player is not null)
        {
            if (_finished is not null)
                player.FinishedPlaying -= _finished;
            player.Stop();
            player.Dispose();
        }
        _finished = null;
        _data?.Dispose();
        _data = null;
    }

    public void Dispose() => Stop();
}
