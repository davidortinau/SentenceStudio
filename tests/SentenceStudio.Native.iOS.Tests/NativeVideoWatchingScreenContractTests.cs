using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// The SwiftUI Video Watching screen keeps the source page (VideoWatching.razor)
/// and its approved boundaries: the only web content is the bridge's
/// privacy-enhanced YouTube player, and the external link goes to the system.
/// </summary>
public sealed class NativeVideoWatchingScreenContractTests
{
    [Fact]
    public void Screen_IsRegisteredForTheVideoWatchingRoute()
    {
        var screens = Source("Features/Activities/ActivityScreens.cs");
        Assert.Contains("NativeActivityCatalog.VideoWatching => new VideoWatchingScreen(host),", screens);
    }

    [Fact]
    public void Player_IsTheBridgeYouTubeEmbedAndNoAppAuthoredWebView()
    {
        var screen = Source("Features/Activities/VideoWatchingScreen.cs");
        Assert.Contains("Views.YouTubePlayer(videoId, title)", screen);
        Assert.DoesNotMatch(@"\bUIKit\b|\bWebKit\b|\bWKWebView\b|\bUIApplication\b|\bSFSafariViewController\b", screen);
        var launcher = Source("Platform/NativeExternalLauncher.cs");
        Assert.Contains("Launcher.Default.OpenAsync(uri)", launcher);
        Assert.DoesNotMatch(@"\bUIKit\b|\bUIApplication\b|Browser\.", launcher);
    }

    [Fact]
    public void Screen_KeepsTheSourceStatesAndControls()
    {
        var screen = Source("Features/Activities/VideoWatchingScreen.cs");
        Assert.Contains("NativeVideoWatchingAccess.OpenInYouTube", screen);
        Assert.Contains("NativeVideoWatchingAccess.TranscriptToggle", screen);
        Assert.Contains("if (state.HasTranscript)", screen);
        Assert.Contains("if (state.ShowTranscript)", screen);
        Assert.Contains(".WithSpeechLanguage(state.TranscriptLanguageTag)", screen);
        Assert.Contains("NativeVideoWatchingAccess.Loading", screen);
        Assert.Contains("NativeVideoWatchingAccess.Error", screen);
        Assert.Contains("NativeVideoWatchingAccess.GoBack", screen);
        Assert.Contains("NativeVideoWatchingText.Spoken(", screen);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(HeadPath, relative));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
