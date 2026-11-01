using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Source InteractiveText/Reading behaviors the SwiftUI Reading screen keeps:
/// passage words are spoken in the passage language, double-tapping a
/// sentence plays from it, and the current sentence scrolls into view.
/// </summary>
public sealed class NativeReadingScreenContractTests
{
    [Fact]
    public void PassageWords_SpeakInThePassageLanguage()
    {
        var screen = Source("Features/Activities/ReadingScreen.cs");
        Assert.Contains("var speech = state.PassageLanguageTag;", screen);
        Assert.Contains(".WithSpeechLanguage(word is null ? null : speech)", screen);
        Assert.Contains(".WithSpeechLanguage(speech)", screen);
    }

    [Fact]
    public void DoubleTappingAWord_PlaysFromItsSentence_OnlyWithLoadedAudio()
    {
        var screen = Source("Features/Activities/ReadingScreen.cs");
        Assert.Contains("if (state.CanNavigate)", screen);
        Assert.Contains("button.OnDoubleTap(() => Fire(token => _presenter.JumpToSentenceAsync(sentenceIndex, token)),",
            screen);
        Assert.Contains("var jumpAction = text(NativeReadingText.PlayFromSentence);", screen);
    }

    [Fact]
    public void CurrentSentence_ScrollsToTheCenter()
    {
        var screen = Source("Features/Activities/ReadingScreen.cs");
        Assert.Contains("state.CurrentSentenceAnchorId is { } anchor", screen);
        Assert.Contains("scroll.ScrollTo(anchor, SwiftScrollAnchor.Center, animated: true);", screen);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(HeadPath, relative));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
