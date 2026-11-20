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

    /// <summary>
    /// Issue 246: every publish (the font size while its slider is dragged, audio progress, the
    /// next sentence) replaced the whole page, so the slider under the finger was a new view
    /// and the drag ended. The mounted page is updated instead and the slider is left alone.
    /// </summary>
    [Fact]
    public void APublish_UpdatesTheMountedPage_AndLeavesTheFontSizeSliderMounted()
    {
        var screen = Source("Features/Activities/ReadingScreen.cs");
        var update = Method(screen, "public bool TryUpdateInPlace(");
        Assert.Contains("Swap(_content, Items(state, style, text));", update);
        Assert.Contains("Swap(_controls, Controls(state, style, text));", update);
        Assert.Contains("_scroll.ScrollTo(anchor, SwiftScrollAnchor.Center, animated: true);", update);
        // Another style (theme, text size), another width or another phase draws the page again.
        Assert.Contains("!ReferenceEquals(style, _renderedStyle)", update);
        // The scene keeps one style and changes its width: the slider shows from 768 pt.
        Assert.Contains("Math.Abs(_renderedWidth - style.ViewportWidth) >= 0.5", update);
        Assert.Contains("state.Phase != NativePhase.Ready || state.SentenceCount == 0", update);
        // The slider is built once with the footer, outside the row an update swaps.
        Assert.DoesNotContain("Footer(", update);
        Assert.Contains("Views.Slider(text(NativeReadingText.FontSize),",
            Method(screen, "private SwiftView Footer("));
        Assert.DoesNotContain("Views.Slider(", Method(screen, "private SwiftView[] Controls("));
        Assert.DoesNotContain("Views.Slider(", Method(screen, "private SwiftView[] Items("));
    }

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing {signature}");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[start..(index + 1)];
        }
        throw new InvalidOperationException($"Unbalanced {signature}");
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(HeadPath, relative)).Replace("\r\n", "\n");

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
