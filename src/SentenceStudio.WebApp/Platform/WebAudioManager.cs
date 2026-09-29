using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

/// <summary>
/// Browser audio is played through JS interop in the Blazor components, not on the web server.
/// </summary>
public sealed class WebAudioPlayerFactory : IAudioPlayerFactory
{
    public bool CanPlayLocally => false;

    public IUiAudioPlayer? CreatePlayer(Stream audioStream) =>
        throw new PlatformNotSupportedException("Audio playback belongs in the browser, not on the web server.");
}
