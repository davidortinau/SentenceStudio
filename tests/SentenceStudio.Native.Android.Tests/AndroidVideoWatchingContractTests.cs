using System.Runtime.CompilerServices;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Compose Video Watching keeps the source page and the approved web view
/// boundary: only the youtube-nocookie embed for a validated id.
/// </summary>
public sealed class AndroidVideoWatchingContractTests
{
    private const string Id = "dQw4w9WgXcQ";

    [Theory]
    [InlineData(Id, true)]
    [InlineData("abc_DEF-123", true)]
    [InlineData("short", false)]
    [InlineData("dQw4w9WgXcQx", false)]
    [InlineData("dQw4w9WgX/Q", false)]
    [InlineData("https://yo", false)]
    [InlineData(null, false)]
    public void VideoIds_AreElevenUrlSafeCharacters(string? id, bool valid) =>
        Assert.Equal(valid, AndroidYouTubeEmbedPolicy.IsVideoId(id));

    [Fact]
    public void Embed_IsThePrivacyEnhancedPlayerOnly()
    {
        Assert.Equal($"https://www.youtube-nocookie.com/embed/{Id}?playsinline=1&rel=0",
            AndroidYouTubeEmbedPolicy.EmbedUrl(Id));
        Assert.Null(AndroidYouTubeEmbedPolicy.EmbedUrl("../evil"));
    }

    [Theory]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?playsinline=1", true, true)]
    [InlineData("https://www.youtube-nocookie.com/embed/otherVideo1", true, false)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true, false)]
    [InlineData("http://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", true, false)]
    [InlineData("https://evil.example/embed/dQw4w9WgXcQ", true, false)]
    [InlineData("https://user@www.youtube-nocookie.com/embed/dQw4w9WgXcQ", true, false)]
    [InlineData("https://www.youtube-nocookie.com:8443/embed/dQw4w9WgXcQ", true, false)]
    [InlineData("https://accounts.google.com/frame", false, true)]
    [InlineData("https://www.youtube.com/s/player", false, true)]
    [InlineData("about:blank", false, true)]
    [InlineData("https://notyoutube.com/x", false, false)]
    [InlineData("http://www.youtube.com/s/player", false, false)]
    [InlineData("file:///sdcard/x", false, false)]
    public void Navigation_StaysOnTheEmbed(string url, bool mainFrame, bool allowed) =>
        Assert.Equal(allowed, AndroidYouTubeEmbedPolicy.AllowsNavigation(
            new Uri(url), mainFrame, Id));

    [Fact]
    public void Shell_WiresTheRouteAndRestrictsTheWebView()
    {
        var activities = Source("AndroidSliceShell.Activities.cs");
        Assert.Contains("case NativeActivityCatalog.VideoWatching:\n                await OpenVideoWatchingAsync(launch, cancellationToken);\n                break;", activities);
        Assert.Contains("case NativeActivityCatalog.VideoWatching:\n                await ExitVideoWatchingAsync(cancellationToken);\n                break;", activities);
        Assert.Contains("NativeActivityCatalog.VideoWatching => RenderVideoWatching(palette),", activities);
        Assert.Contains("ReleaseVideoWatching();", activities);
        var screen = Source("AndroidSliceShell.VideoWatching.Compose.cs");
        Assert.Contains("new AndroidView(", screen);
        Assert.Contains(".AspectRatio(16f / 9f)", screen);
        Assert.Contains("settings.AllowFileAccess = false;", screen);
        Assert.Contains("settings.AllowContentAccess = false;", screen);
        Assert.Contains("settings.SetSupportMultipleWindows(false);", screen);
        Assert.Contains("AndroidYouTubeEmbedPolicy.AllowsNavigation(", screen);
        Assert.DoesNotContain("AddJavascriptInterface", screen);
        Assert.DoesNotContain("LoadData", screen);
        Assert.Contains("NativeVideoWatchingAccess.TranscriptToggle", screen);
        Assert.Contains("NativeVideoWatchingAccess.OpenInYouTube", screen);
        Assert.Contains("NativeVideoWatchingAccess.GoBack", screen);
    }

    private static string Source(string file) => File.ReadAllText(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.Android", file)))
        .Replace("\r\n", "\n");

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
