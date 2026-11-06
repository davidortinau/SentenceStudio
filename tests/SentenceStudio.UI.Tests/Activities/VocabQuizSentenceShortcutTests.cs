using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backport B70 (Captain, 2026-11-06: "I shouldn't see the translation"). "Use in a sentence"
/// shows the target word only while the learner writes with it: no native-language term on the
/// card, in its labels or placeholder, and no prompt audio, which in NativeToTarget speaks the
/// translation. The native heads match (departure 29 retired).
/// </summary>
public sealed class VocabQuizSentenceShortcutTests
{
    private const string Target = "과일";
    private const string Native = "fruit";

    [Fact]
    public void TheCard_NamesTheTargetWordOnly()
    {
        var card = SentenceCardMarkup();

        card.Should().Contain("@currentItem.Word.TargetLanguageTerm");
        card.Should().NotContain("NativeLanguageTerm", "the translation is not shown while writing");
        card.Should().NotContain("PlayWordAudio").And.NotContain("PlayChoiceAudio")
            .And.NotContain("bi-volume", "the card offers no prompt audio");
        card.Should().Contain("@GetSentenceShortcutInstruction()");
        card.Should().Contain("aria-label=\"@GetSentenceShortcutInstruction()\"",
            "the editor is labelled with the instruction, which names the target word only");
        card.Should().Contain("placeholder=\"@GetSentenceShortcutPlaceholder()\"");
    }

    [Fact]
    public void TheInstructionAndPlaceholder_NameTheTargetWordOnly()
    {
        var quiz = Quiz();
        Set(quiz, "Localize", new BlazorLocalizationService());

        foreach (var method in new[] { "GetSentenceShortcutInstruction", "GetSentenceShortcutPlaceholder" })
        {
            var text = (string)Method(method).Invoke(quiz, null)!;
            text.Should().Contain(Target, method);
            text.Should().NotContain(Native, method);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningTheShortcut_StopsAPromptClipThatIsPlaying(bool canPlayLocally)
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        harness.Audio.CanPlayLocally = canPlayLocally;
        var quiz = Quiz(harness);
        // NativeToTarget: the prompt clip speaks the translation.
        await (Task)Method("PlayWordAudio").Invoke(quiz, null)!;
        harness.Speech.SynthesisCount.Should().Be(1);

        await (Task)Method("OpenSentenceShortcut").Invoke(quiz, null)!;

        Get<bool>(quiz, "isSentenceShortcutMode").Should().BeTrue();
        if (canPlayLocally)
            harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal("play@1", "stop");
        else
            harness.Js.Calls.Select(call => call.Identifier).Should().EndWith("audioInterop.stop");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClipStillLoadingWhenTheShortcutOpens_NeverPlays(bool canPlayLocally)
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        harness.Audio.CanPlayLocally = canPlayLocally;
        harness.Speech.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiz = Quiz(harness);
        var play = (Task)Method("PlayWordAudio").Invoke(quiz, null)!;
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await (Task)Method("OpenSentenceShortcut").Invoke(quiz, null)!;
        harness.Speech.Hold.SetResult();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Audio.Players.Should().BeEmpty();
        harness.Js.Calls.Should().NotContain(call => call.Identifier == "audioInterop.loadFromBase64"
            || call.Identifier == "audioInterop.startPlayback");
        Get<bool>(quiz, "isPlayingAudio").Should().BeFalse("Cancel can play the prompt again");
    }

    [Fact]
    public async Task NoPromptAudioStartsInTheShortcut()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var quiz = Quiz(harness);
        Set(quiz, "isSentenceShortcutMode", true);

        await (Task)Method("PlayWordAudio").Invoke(quiz, null)!;

        harness.Speech.Requests.Should().BeEmpty();
        harness.Audio.Players.Should().BeEmpty();
        harness.Js.Calls.Should().BeEmpty();
    }

    /// <summary>The quiz on a NativeToTarget turn, with only what audio and the shortcut read.</summary>
    private static VocabQuiz Quiz(ActivityPageHarness? harness = null)
    {
        var quiz = new VocabQuiz();
        Set(quiz, "currentItem", new VocabularyQuizItem
        {
            Word = new VocabularyWord
            {
                Id = Guid.NewGuid().ToString(),
                TargetLanguageTerm = Target,
                NativeLanguageTerm = Native,
                Language = "Korean",
            }
        });
        Set(quiz, "promptUsesNativeLanguage", true);
        if (harness is null)
            return quiz;

        var services = harness.Services;
        Set(quiz, "StreamHistoryRepo", services.GetRequiredService<StreamHistoryRepository>());
        Set(quiz, "FileSystemService", services.GetRequiredService<IFileSystemService>());
        Set(quiz, "Connectivity", services.GetRequiredService<IConnectivityService>());
        Set(quiz, "SpeechService", services.GetRequiredService<ElevenLabsSpeechService>());
        Set(quiz, "VoicePrefs", services.GetRequiredService<SpeechVoicePreferences>());
        Set(quiz, "AudioPlayerFactory", services.GetRequiredService<IAudioPlayerFactory>());
        Set(quiz, "JS", services.GetRequiredService<IJSRuntime>());
        Set(quiz, "Localize", new BlazorLocalizationService());
        Set(quiz, "Toast", services.GetRequiredService<ToastService>());
        Set(quiz, "Logger", NullLogger<VocabQuiz>.Instance);
        return quiz;
    }

    /// <summary>The sentence card: from the shortcut branch to the end of its editor form.</summary>
    private static string SentenceCardMarkup()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "SentenceStudio.UI", "Pages", "VocabQuiz.razor"));
        var start = source.IndexOf("@if (isSentenceShortcutMode)", StringComparison.Ordinal);
        var end = source.IndexOf("</form>", start, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        // Razor comments are not rendered.
        return Regex.Replace(source[start..end], @"@\*.*?\*@", "", RegexOptions.Singleline);
    }

    private static MethodInfo Method(string name) =>
        typeof(VocabQuiz).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(VocabQuiz), name);

    private static T Get<T>(object page, string name) =>
        (T)typeof(VocabQuiz).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static void Set(object page, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = page.GetType();
        if (type.GetProperty(name, flags) is { } property)
            property.SetValue(page, value);
        else if (type.GetField(name, flags) is { } field)
            field.SetValue(page, value);
        else
            throw new MissingMemberException(type.Name, name);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
