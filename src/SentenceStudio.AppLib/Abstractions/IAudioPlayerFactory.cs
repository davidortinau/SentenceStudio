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
}
