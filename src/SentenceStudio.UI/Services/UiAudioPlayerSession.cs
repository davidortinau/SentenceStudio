using SentenceStudio.Abstractions;

namespace SentenceStudio.WebUI.Services;

/// <summary>Keeps the source stream alive for as long as its native player is in use.</summary>
public sealed class UiAudioPlayerSession : IDisposable
{
    private IUiAudioPlayer? _player;
    private Stream? _stream;

    public IUiAudioPlayer? Player => _player;

    public IUiAudioPlayer CreatePlayer(IAudioPlayerFactory factory, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            Stop();
            if (!factory.CanPlayLocally)
                throw new PlatformNotSupportedException("This host requires browser audio playback.");

            _player = factory.CreatePlayer(stream)
                ?? throw new InvalidOperationException("A native audio player is unavailable.");
            _stream = stream;
            return _player;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        var player = _player;
        var stream = _stream;
        _player = null;
        _stream = null;

        try
        {
            player?.Stop();
        }
        finally
        {
            try
            {
                player?.Dispose();
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }

    public void Dispose() => Stop();
}
