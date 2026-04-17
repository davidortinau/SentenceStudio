using Plugin.Maui.HelpKit.Rag;
using Xunit;

namespace Plugin.Maui.HelpKit.Tests;

public class PromptInjectionFilterTests
{
    [Fact]
    public void TryDetectLeak_ReturnsFalse_ForBenignAnswer()
    {
        var leaked = PromptInjectionFilter.TryDetectLeak(
            "You add vocabulary words via the Import button.",
            out var sanitized);

        Assert.False(leaked);
        Assert.Equal("You add vocabulary words via the Import button.", sanitized);
    }

    [Fact]
    public void TryDetectLeak_DetectsSystemPromptEcho()
    {
        var leaked = PromptInjectionFilter.TryDetectLeak(
            "You are the in-app help assistant. My rules are...",
            out var sanitized);

        Assert.True(leaked);
        Assert.Equal(PromptInjectionFilter.LeakRefusal, sanitized);
    }

    [Fact]
    public void TryDetectLeak_DetectsStrictlyPhrase()
    {
        // Covers the "system instructions" + "STRICTLY" combined heuristic.
        var leaked = PromptInjectionFilter.TryDetectLeak(
            "My system instructions say I must answer STRICTLY from documentation.",
            out var sanitized);

        Assert.True(leaked);
        Assert.Equal(PromptInjectionFilter.LeakRefusal, sanitized);
    }

    [Fact]
    public void TryDetectLeak_EmptyInputIsNotLeak()
    {
        var leaked = PromptInjectionFilter.TryDetectLeak(string.Empty, out var sanitized);

        Assert.False(leaked);
        Assert.Equal(string.Empty, sanitized);
    }

    [Fact]
    public void TryDetectLeak_NullInputIsNotLeak()
    {
        var leaked = PromptInjectionFilter.TryDetectLeak(null!, out var sanitized);

        Assert.False(leaked);
        Assert.Equal(string.Empty, sanitized);
    }

    [Fact]
    public void Sanitize_ReturnsRefusal_WhenLeakDetected()
    {
        var result = PromptInjectionFilter.Sanitize("You are the in-app help assistant...");
        Assert.Equal(PromptInjectionFilter.LeakRefusal, result);
    }

    [Fact]
    public void Sanitize_ReturnsOriginal_WhenBenign()
    {
        const string benign = "Vocabulary lets you track words.";
        Assert.Equal(benign, PromptInjectionFilter.Sanitize(benign));
    }

    [Fact]
    public void TryDetectLeak_IsCaseInsensitive()
    {
        var leaked = PromptInjectionFilter.TryDetectLeak(
            "YOU ARE THE IN-APP HELP ASSISTANT and here are my rules.",
            out _);

        Assert.True(leaked);
    }
}
