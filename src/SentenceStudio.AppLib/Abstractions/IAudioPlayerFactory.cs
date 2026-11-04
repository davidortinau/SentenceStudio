#nullable enable

namespace SentenceStudio.Abstractions;

public interface IAudioPlayerFactory
{
    bool CanPlayLocally { get; }

    IUiAudioPlayer? CreatePlayer(Stream audioStream);
}

public interface IUiAudioPlayer : IDisposable
{
    event EventHandler? PlaybackEnded;

    bool IsPlaying { get; }
    double CurrentPosition { get; }
    double Duration { get; }

    void Play();
    void Pause();
    void Stop();
    void Seek(double seconds);

    /// <summary>
    /// Plays at <paramref name="rate"/> times normal speed (pitch preserved where
    /// the platform supports it). False when this player cannot change its rate.
    /// </summary>
    bool TrySetPlaybackRate(double rate) => false;
}
