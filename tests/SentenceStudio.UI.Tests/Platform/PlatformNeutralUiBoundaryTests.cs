namespace SentenceStudio.UI.Tests.Platform;

public class PlatformNeutralUiBoundaryTests
{
    [Theory]
    [InlineData("VocabularyWordEdit")]
    [InlineData("NumberDrill")]
    [InlineData("VocabQuiz")]
    [InlineData("MinimalPairSession")]
    [InlineData("HowDoYouSay")]
    [InlineData("Shadowing")]
    [InlineData("Conversation")]
    public void AudioPages_UseNeutralNativePlayerAndRealBrowserPlayback(string page)
    {
        var source = ReadSource($"src/SentenceStudio.UI/Pages/{page}.razor");

        source.Should().Contain("IAudioPlayerFactory");
        source.Should().Contain("AudioPlayerFactory.CanPlayLocally");
        source.Should().Contain("UiAudioPlayerSession");
        source.Should().Contain("BrowserAudioPlayback.Play");
        source.Should().NotContain("Plugin.Maui.Audio");
        source.Should().NotContain("IAudioManager");
    }

    [Fact]
    public void Reading_RoutesAudioToBrowserOnlyWhenNoLocalPlayerExists()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/Reading.razor");

        source.Should().Contain("_useJsAudio = !AudioPlayerFactory.CanPlayLocally");
        source.Should().Contain("else if (!_audioManager.HasNativePlayer)");
        source.Should().Contain("new TimestampedAudioManager(_timingCalculator, AudioPlayerFactory, AudioManagerLogger)");
        source.Should().Contain("audioInterop.loadFromBase64");
        source.Should().Contain("audioInterop.seekTo");
        source.Should().Contain("audioInterop.pause");
        source.Should().Contain("audioInterop.stop");
        source.Should().NotContain("Plugin.Maui.Audio");
        source.Should().NotContain("GetType().Name.Contains(\"Remote\"");
    }

    [Fact]
    public void Conversation_CancelsPendingAudioAndReceivesBrowserPlaybackEnd()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/Conversation.razor");

        source.Should().Contain("BrowserAudioPlayback.PlayAndTrackAsync");
        source.Should().Contain("OnJsPlaybackEnded()");
        source.Should().Contain("OnJsTimeUpdate(double _)");
        source.Should().Contain("request?.Cancel()");
        source.Should().Contain("audioInterop.stop");
    }

    [Theory]
    [InlineData("Layout/NavMenu.razor", "nativeAuth.LogOutAsync()")]
    [InlineData("Pages/Onboarding.razor", "nativeAuth.LogOutAsync()")]
    [InlineData("Pages/LoginPage.razor", "nativeAuth.NotifySignedIn()")]
    public void OptionalNativeAuthUsesNeutralNotificationContract(string uiPath, string call)
    {
        var source = ReadSource($"src/SentenceStudio.UI/{uiPath}");

        source.Should().Contain("AuthStateProvider is IClientAuthStateNotifier nativeAuth");
        source.Should().Contain(call);
        source.Should().NotContain("MauiAuthenticationStateProvider");
    }

    [Fact]
    public void DashboardSyncChecksHostInsteadOfMauiDeviceInfo()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/Index.razor");

        source.Should().Contain("BlazorHostKind.IsWebViewHost(NavManager.BaseUri)");
        source.Should().NotContain("DeviceInfo.Platform");
    }

    [Fact]
    public void MinimalPairCreationUsesPortablePreferences()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/MinimalPairCreate.razor");

        source.Should().Contain("IPreferencesService Preferences");
        source.Should().Contain("Preferences.Get(\"active_profile_id\", \"\")");
        source.Should().NotContain("private IPreferences Preferences");
    }

    [Fact]
    public void WebAppRegistersAnExplicitNonLocalAudioFactory()
    {
        var source = ReadSource("src/SentenceStudio.WebApp/Program.cs");

        source.Should().Contain("AddSingleton<IAudioPlayerFactory, WebAudioPlayerFactory>()");
        source.Should().NotContain("WebAudioManagerProxy.Create()");
    }

    [Fact]
    public void WebShellLoadsTheBrowserAudioFunctionsUsedBySharedPages()
    {
        ReadSource("src/SentenceStudio.WebApp/Components/App.razor")
            .Should().Contain("<script src=\"js/audioInterop.js\"></script>");

        var interop = ReadSource("src/SentenceStudio.WebApp/wwwroot/js/audioInterop.js");
        foreach (var function in new[]
        {
            "loadFromBase64", "play", "pause", "seekTo", "isPlaying",
            "startTimeTracking", "stop"
        })
        {
            interop.Should().Contain($"{function}: function");
        }
    }

    private static string ReadSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;

        dir.Should().NotBeNull();
        return File.ReadAllText(Path.Combine(dir!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
