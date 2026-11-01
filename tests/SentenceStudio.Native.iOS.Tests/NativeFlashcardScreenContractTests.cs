using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// The SwiftUI Flashcard Preview keeps the source swipe (FlashcardActivity.razor)
/// like the Android Compose screen: the card follows a horizontal drag with the
/// shared rotation, a release at the shared 110 pt threshold answers through the
/// presenter commands, a short drag springs back, a tap still flips, and the
/// Incorrect/Correct buttons stay as the accessible path. SwiftUI only.
/// </summary>
public sealed class NativeFlashcardScreenContractTests
{
    [Fact]
    public void Card_FollowsTheDragThroughTheBridgeOnTheMountedNode()
    {
        var screen = Source("Features/Activities/FlashcardScreen.cs");
        Assert.Contains("button.OnHorizontalDrag(", screen);
        Assert.Contains(
            "card.WithTransform(translationX, 0, NativeFlashcardGesture.RotationDegrees(translationX));",
            screen);
        // Tracking the finger updates the node directly; it never re-renders the screen.
        var changed = Method(screen, "private void OnCardDragChanged(");
        Assert.DoesNotContain("Publish", changed);
        Assert.DoesNotContain("Fire(", changed);
        Assert.DoesNotMatch(@"\bUIKit\b|\bUIPanGestureRecognizer\b|\bUIView\b", screen);
    }

    [Fact]
    public void Release_UsesTheSharedThresholdsAndPresenterCommands()
    {
        var ended = Method(Source("Features/Activities/FlashcardScreen.cs"), "private void OnCardDragEnded(");
        Assert.Contains("switch (NativeFlashcardGesture.Classify(translationX))", ended);
        Assert.Contains(
            "case NativeFlashcardRelease.Correct:\n                Fire(token => _presenter.AnswerAsync(true, token));",
            ended);
        Assert.Contains(
            "case NativeFlashcardRelease.Incorrect:\n                Fire(token => _presenter.AnswerAsync(false, token));",
            ended);
        Assert.Contains("Fire(_presenter.FlipAsync);", ended);
        // Between the flip and swipe thresholds the card springs back.
        Assert.Contains("default:\n                card.WithTransform(0, animated: true);", ended);
        Assert.Equal(110d, NativeFlashcardGesture.SwipeThreshold);
    }

    [Fact]
    public void TapFlips_ButtonsRemain_AndTheThrowAnimatesTheSameCard()
    {
        var screen = Source("Features/Activities/FlashcardScreen.cs");
        Assert.Contains("Views.Button(surface, () => Fire(_presenter.FlipAsync))", screen);
        Assert.Contains("_presenter.AnswerAsync(false, token)), fullWidth: true)", screen);
        Assert.Contains("_presenter.AnswerAsync(true, token)), fullWidth: true)", screen);
        Assert.Contains(".WithAccessibilityIdentifier(NativeFlashcardAccess.Incorrect)", screen);
        Assert.Contains(".WithAccessibilityIdentifier(NativeFlashcardAccess.Correct)", screen);
        var inPlace = Method(screen, "public bool TryUpdateInPlace(");
        Assert.Contains("ThrowCard(_card, pending, animated: true);", inPlace);
        Assert.Contains("_card.IsEnabled = false;", inPlace);
        Assert.Contains("_incorrect.IsEnabled = false;", inPlace);
        Assert.Contains("_correct.IsEnabled = false;", inPlace);
        Assert.Contains("NativeFlashcardGesture.ThrowDistance", Method(screen, "private static void ThrowCard("));
        Assert.Contains("OnCardDragCancelled(button)", screen);
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
