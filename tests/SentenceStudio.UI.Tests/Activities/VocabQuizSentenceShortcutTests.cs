using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.LanguageSegmentation;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backport B70 (Captain, 2026-11-06: "I shouldn't see the translation"). "Use in a sentence"
/// shows the target word only while the learner writes with it: no native-language term on the
/// card, in its labels or placeholder, and no prompt audio, which in NativeToTarget speaks the
/// translation. Every prompt-context change (the shortcut, the next turn, the round summary,
/// Back) ends the prompt's audio as a deliberate stop: a clip still being made is cached but not
/// played, one the browser is loading or starting is stopped there too, and the turn keeps its
/// audio prompt and audio-answer mode. Runs the real page on a NativeToTarget turn, in English.
/// </summary>
public sealed class VocabQuizSentenceShortcutTests
{
    private static readonly (string Target, string Native)[] Words =
        [("과일", "fruit"), ("사과", "apple"), ("학교", "school"), ("우유", "milk")];

    [Fact]
    public async Task TheCard_ShowsNoTranslationOrPromptAudio_BeforeOrAfterGrading()
    {
        await using var harness = await OpenQuizAsync();
        var word = harness.Get<VocabularyQuizItem>("currentItem").Word;
        var turn = harness.Html();
        turn.Should().Contain(word.NativeLanguageTerm, "the NativeToTarget prompt is the native term");
        PlayPromptButton(turn).Should().NotBeEmpty("the turn offers its audio prompt");

        await harness.DispatchAsync("OpenSentenceShortcut");

        var card = harness.Html();
        AssertTargetOnly(card, word);
        AssertNoPromptAudioControl(card);
        var instruction = Regex.Match(card,
            @"<p id=""vocabquiz-sentence-instruction""[^>]*>\s*(?<text>[^<]*?)\s*</p>");
        instruction.Success.Should().BeTrue();
        instruction.Groups["text"].Value.Should().Be($"Write one or more sentences using {word.TargetLanguageTerm}.");
        var editor = Regex.Match(card, @"<textarea[^>]*>").Value;
        editor.Should().Contain("id=\"vocabquiz-sentence-editor\"")
            .And.Contain("aria-labelledby=\"vocabquiz-sentence-instruction\"")
            .And.Contain(word.TargetLanguageTerm, "the placeholder names the target word");

        await ShowGradedResultAsync(harness, word.TargetLanguageTerm);

        var graded = harness.Html();
        AssertTargetOnly(graded, word);
        AssertNoPromptAudioControl(graded);
        graded.Should().Contain($"Graded: {word.TargetLanguageTerm}");
    }

    [Theory]
    [InlineData("audioInterop.loadFromBase64")]
    [InlineData("audioInterop.startPlayback")]
    public async Task OpeningTheShortcutWhileTheBrowserLoadsOrStartsTheClip_IsAStopNotAFailure(string held)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally: false);
        var promptAudio = harness.Get<bool>("audioPromptAvailable");
        var answerAudio = harness.Get<bool>("audioAnswerModeAvailable");
        promptAudio.Should().BeTrue();
        harness.Js.Hold = held;

        var play = harness.DispatchAsync("PlayWordAudio");
        await harness.Js.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DispatchAsync("OpenSentenceShortcut");
        harness.Js.Release();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Js.Played.Should().BeEmpty("the prompt clip never plays in the shortcut");
        AssertStopSentAfter(harness, held);
        harness.Get<bool>("audioPromptAvailable").Should().Be(promptAudio, "the turn keeps its audio prompt");
        harness.Get<bool>("audioAnswerModeAvailable").Should().Be(answerAudio);
        harness.Get<bool>("isPlayingAudio").Should().BeFalse();
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        harness.Toasts.Should().BeEmpty();
    }

    /// <summary>
    /// Cancelling the request ends only the .NET await: the browser goes on loading or starting
    /// turn N's clip, so it must be told to stop, or the clip plays over turn N+1.
    /// </summary>
    [Theory]
    [InlineData("audioInterop.loadFromBase64")]
    [InlineData("audioInterop.startPlayback")]
    public async Task TheTurnChangingWhileTheBrowserLoadsOrStartsTheClip_StopsItThere(string held)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally: false);
        var first = harness.Get<VocabularyQuizItem>("currentItem").Word.Id;
        harness.Js.Hold = held;

        var play = harness.DispatchAsync("PlayWordAudio");
        await harness.Js.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Set("currentTurnInRound", 1);
        await harness.DispatchAsync("LoadCurrentItem", false);
        harness.Get<VocabularyQuizItem>("currentItem").Word.Id.Should().NotBe(first);
        harness.Js.Release();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Js.Played.Should().BeEmpty("turn N's clip never plays on turn N+1");
        AssertStopSentAfter(harness, held);
        harness.Get<bool>("audioPromptAvailable").Should().BeTrue("the new turn keeps its audio prompt");
        harness.Get<bool>("isPlayingAudio").Should().BeFalse("the new turn can play its own prompt");
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        harness.Toasts.Should().BeEmpty();
    }

    /// <summary>
    /// Synthesis is not part of the playback: a clip still being made when the shortcut opens is
    /// finished and cached, so playing the prompt after Cancel needs no second (paid) synthesis.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClipStillLoadingWhenTheShortcutOpens_NeverPlays_AndIsCachedForLater(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        harness.Speech.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var play = harness.DispatchAsync("PlayWordAudio");
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DispatchAsync("OpenSentenceShortcut");
        harness.Speech.Hold.SetResult();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        AssertNothingPlayed(harness);
        harness.Speech.SynthesisCount.Should().Be(1, "the synthesis finished although its playback was dropped");
        (await harness.WithDbAsync(db => db.StreamHistories.CountAsync())).Should().Be(1);
        harness.Get<bool>("audioPromptAvailable").Should().BeTrue();
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        harness.Toasts.Should().BeEmpty();

        await harness.DispatchAsync("CancelSentenceShortcut");
        await harness.DispatchAsync("PlayWordAudio");

        harness.Speech.SynthesisCount.Should().Be(1, "the cached clip plays");
        if (canPlayLocally)
            harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal("play@1");
        else
            harness.Js.Played.Should().ContainSingle();
    }

    /// <summary>
    /// Synthesis outlives its playback, so Play after Cancel while the first clip is still being
    /// made must wait for that clip: a second request would pay for a second synthesis and store
    /// a second row and file for the same phrase and voice.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlayAfterCancelWhileTheFirstClipIsStillMade_SharesItsSynthesis(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        harness.Speech.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = harness.DispatchAsync("PlayWordAudio");
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DispatchAsync("OpenSentenceShortcut");
        await harness.DispatchAsync("CancelSentenceShortcut");
        var second = harness.DispatchAsync("PlayWordAudio");
        // A second request, if one is made, reaches the API at once.
        await WaitForAsync(() => harness.Speech.RequestsStarted >= 2, TimeSpan.FromSeconds(1));

        harness.Speech.RequestsStarted.Should().Be(1, "the second Play waits for the clip the first is making");
        harness.Speech.Hold.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await second.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Speech.SynthesisCount.Should().Be(1);
        (await harness.WithDbAsync(db => db.StreamHistories.CountAsync())).Should().Be(1);
        Directory.GetFiles(harness.AudioCacheDirectory).Should().ContainSingle();
        if (canPlayLocally)
            harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal("play@1");
        else
            harness.Js.Played.Should().ContainSingle();
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    /// <summary>
    /// The turn change awaits the browser's stop before it moves on. A Play tap handled in that
    /// wait is refused: it would start the old turn's clip after the stop and take the new
    /// turn's autoplay.
    /// </summary>
    [Fact]
    public async Task APlayTapWhileTheTurnChangeStopsTheBrowserClip_IsRefused()
    {
        await using var harness = await OpenQuizAsync(canPlayLocally: false, autoPlay: true);
        await harness.WaitUntilAsync(() => harness.Js.Played.Count == 1 && !harness.Get<bool>("isPlayingAudio"));
        byte[] nextTurnClip = [0x49, 0x44, 0x33, 0x04, 0x02];
        harness.Speech.Audio = nextTurnClip;
        harness.Js.Hold = "audioInterop.stop";

        harness.Set("currentTurnInRound", 1);
        var change = harness.DispatchAsync("LoadCurrentItem", false);
        await harness.Js.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopAt = LastCall(harness, "audioInterop.stop");
        await harness.DispatchAsync("PlayWordAudio");
        harness.Js.Release();
        await change.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.WaitUntilAsync(() => harness.Js.Played.Count >= 2 && !harness.Get<bool>("isPlayingAudio"));

        harness.Js.Calls.Skip(stopAt + 1).Where(call => call.Identifier == "audioInterop.loadFromBase64")
            .Select(call => (string)call.Args[0]!).Should().Equal([Convert.ToBase64String(nextTurnClip)],
                "only the new turn's autoplay loads a clip after the stop");
    }

    [Fact]
    public async Task APlayTapWhileTheSummaryStopsTheBrowserClip_IsRefused()
    {
        await using var harness = await OpenQuizAsync(canPlayLocally: false);
        MakeTheLastWordRotateOut(harness);
        await harness.DispatchAsync("PlayWordAudio");
        harness.Js.Played.Should().ContainSingle();
        harness.Js.Hold = "audioInterop.stop";

        var next = harness.DispatchAsync("NextItem");
        await harness.Js.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopAt = LastCall(harness, "audioInterop.stop");
        await harness.DispatchAsync("PlayWordAudio");
        harness.Js.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Get<bool>("showSessionSummary").Should().BeTrue();
        harness.Js.Calls.Skip(stopAt + 1).Should().NotContain(call => call.Identifier == "audioInterop.loadFromBase64"
            || call.Identifier == "audioInterop.startPlayback", "nothing plays over the summary");
        harness.Js.Played.Should().ContainSingle();
    }

    /// <summary>
    /// A request the context has left can still fail with something other than a stop (here the
    /// store's owner check cannot read the profile table). That failure is not the current
    /// turn's: its clip keeps playing, it keeps its audio prompt, and nothing is logged as an error.
    /// </summary>
    [Fact]
    public async Task ASupersededRequestThatFails_LeavesTheCurrentTurnAlone()
    {
        await using var harness = await OpenQuizAsync(canPlayLocally: true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Speech.Hold = held;
        var stale = harness.DispatchAsync("PlayWordAudio");
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var staleWord = harness.Get<VocabularyQuizItem>("currentItem").Word;
        harness.Speech.Hold = null;
        harness.Set("currentTurnInRound", 1);
        await harness.DispatchAsync("LoadCurrentItem", false);
        await harness.DispatchAsync("PlayWordAudio");
        harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal("play@1");
        var answerAudio = harness.Get<bool>("audioAnswerModeAvailable");

        await harness.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"UserProfile\" RENAME TO \"UserProfile_unavailable\""));
        try
        {
            held.SetResult();
            await stale.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await harness.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"UserProfile_unavailable\" RENAME TO \"UserProfile\""));
        }

        harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal(["play@1"],
            "the current turn's clip plays on");
        harness.Get<bool>("audioPromptAvailable").Should().BeTrue("the current turn keeps its audio prompt");
        harness.Get<bool>("audioAnswerModeAvailable").Should().Be(answerAudio);
        var pageEntries = harness.Logs.Entries.Where(entry => entry.Message.Contains("quiz audio", StringComparison.OrdinalIgnoreCase)).ToList();
        pageEntries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        pageEntries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("Exception").And.NotContain(staleWord.NativeLanguageTerm)
            .And.NotContain(staleWord.TargetLanguageTerm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClipStillLoadingWhenTheTurnChanges_DoesNotPlayOnTheNextTurn(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        var first = harness.Get<VocabularyQuizItem>("currentItem").Word.Id;
        harness.Speech.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var play = harness.DispatchAsync("PlayWordAudio");
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Set("currentTurnInRound", 1);
        await harness.DispatchAsync("LoadCurrentItem", false);
        harness.Get<VocabularyQuizItem>("currentItem").Word.Id.Should().NotBe(first);
        harness.Speech.Hold.SetResult();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        AssertNothingPlayed(harness);
        harness.Get<bool>("audioPromptAvailable").Should().BeTrue("the new turn keeps its audio prompt");
        harness.Get<bool>("isPlayingAudio").Should().BeFalse("the new turn can play its own prompt");
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    /// <summary>The last word rotating out goes straight to the round summary, not through LoadCurrentItem.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClipStillLoadingWhenTheLastWordGoesToTheSummary_NeverPlaysOverIt(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        MakeTheLastWordRotateOut(harness);
        harness.Speech.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var play = harness.DispatchAsync("PlayWordAudio");
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DispatchAsync("NextItem");
        harness.Get<bool>("showSessionSummary").Should().BeTrue();
        harness.Speech.Hold.SetResult();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        AssertNothingPlayed(harness);
        harness.Get<bool>("isPlayingAudio").Should().BeFalse();
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLastWordGoingToTheSummary_StopsItsClip(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        MakeTheLastWordRotateOut(harness);
        Task play;
        if (canPlayLocally)
        {
            play = harness.DispatchAsync("PlayWordAudio");
            await play;
        }
        else
        {
            // The browser is starting the clip (play() not yet resolved).
            harness.Js.Hold = "audioInterop.startPlayback";
            play = harness.DispatchAsync("PlayWordAudio");
            await harness.Js.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await harness.DispatchAsync("NextItem");
        harness.Get<bool>("showSessionSummary").Should().BeTrue();
        harness.Js.Release();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        if (canPlayLocally)
        {
            harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal("play@1", "stop");
        }
        else
        {
            harness.Js.Played.Should().BeEmpty("the clip never plays over the summary");
            AssertStopSentAfter(harness, "audioInterop.startPlayback");
        }
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningTheShortcut_StopsAPromptClipThatIsPlaying(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        await harness.DispatchAsync("PlayWordAudio");
        harness.Speech.SynthesisCount.Should().Be(1);

        await harness.DispatchAsync("OpenSentenceShortcut");

        if (canPlayLocally)
            harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal("play@1", "stop");
        else
            harness.Js.Calls.Select(call => call.Identifier).Should().EndWith("audioInterop.stop");
    }

    /// <summary>
    /// Autoplay starts outside an event handler, so the page is drawn again when it ends;
    /// otherwise the Play buttons stay disabled until something else redraws it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AfterAutoplayEnds_ThePlayButtonIsEnabledAgain(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally, autoPlay: true,
            beforeRender: h => h.Speech.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        PlayPromptButton(harness.Html()).Should().Contain("disabled", "autoplay is playing the prompt");

        harness.Speech.Hold!.SetResult();
        await harness.WaitUntilAsync(() => !harness.Get<bool>("isPlayingAudio"));

        await WaitForHtmlAsync(harness, html => !PlayPromptButton(html).Contains("disabled"));
        if (canPlayLocally)
            harness.Audio.Players.Should().ContainSingle().Which.Events.Should().Equal("play@1");
        else
            harness.Js.Played.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoPromptOrChoiceAudioStartsInTheShortcut(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        await harness.DispatchAsync("OpenSentenceShortcut");

        // An autoplay queued before the shortcut opened, and the turn's choice clips.
        await harness.DispatchAsync("PlayWordAudio");
        foreach (var option in harness.Get<Array>("choiceOptions"))
            await harness.DispatchAsync("PlayChoiceAudio", option);

        harness.Speech.Requests.Should().BeEmpty();
        AssertNothingPlayed(harness);
    }

    private static void AssertTargetOnly(string html, VocabularyWord word)
    {
        html.Should().Contain(word.TargetLanguageTerm);
        foreach (var (_, native) in Words)
            Regex.IsMatch(html, $@"\b{Regex.Escape(native)}\b", RegexOptions.IgnoreCase)
                .Should().BeFalse($"no translation (\"{native}\") is on the page while the learner writes");
    }

    /// <summary>No Play prompt (PlayWordAudio) or Play option (PlayChoiceAudio) button, and no volume icon.</summary>
    private static void AssertNoPromptAudioControl(string html)
    {
        PlayPromptButton(html).Should().BeEmpty("the card offers no prompt audio");
        html.Should().NotContain("aria-label=\"Play option", "the card offers no choice audio")
            .And.NotContain("bi-volume", "the card has no audio control");
    }

    /// <summary>The Play prompt button's start tag, or empty when the page has none.</summary>
    private static string PlayPromptButton(string html) =>
        Regex.Match(html, @"<button[^>]*aria-label=""Play audio""[^>]*>").Value;

    private static void AssertNothingPlayed(ActivityPageHarness harness)
    {
        harness.Audio.Players.Should().BeEmpty();
        harness.Js.Played.Should().BeEmpty();
        harness.Js.Calls.Should().NotContain(call => call.Identifier == "audioInterop.loadFromBase64"
            || call.Identifier == "audioInterop.startPlayback");
    }

    /// <summary>The browser was told to stop after the call it was still running.</summary>
    private static void AssertStopSentAfter(ActivityPageHarness harness, string held)
    {
        var calls = harness.Js.Calls.Select(call => call.Identifier).ToList();
        var heldAt = calls.IndexOf(held);
        heldAt.Should().BeGreaterThanOrEqualTo(0);
        calls.Skip(heldAt + 1).Should().Contain("audioInterop.stop",
            "a cancelled request leaves the browser loading or starting the clip");
    }

    /// <summary>The current word is the round's last and is ready to rotate out, so Next goes to the summary.</summary>
    private static void MakeTheLastWordRotateOut(ActivityPageHarness harness)
    {
        var item = harness.Get<VocabularyQuizItem>("currentItem");
        item.RequiresFullSessionDemonstration = false;
        item.PendingRecognitionCheck = false;
        item.SessionMCCorrect = 3;
        item.SessionTextCorrect = 3;
        item.ReadyToRotateOut.Should().BeTrue();
        harness.Set("roundWordOrder", new List<VocabularyQuizItem> { item });
        harness.Set("currentTurnInRound", 0);
    }

    private static int LastCall(ActivityPageHarness harness, string identifier) =>
        harness.Js.Calls.FindLastIndex(call => call.Identifier == identifier);

    /// <summary>Waits until the condition holds or the time is up, whichever comes first.</summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private static async Task WaitForHtmlAsync(ActivityPageHarness harness, Func<string, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition(harness.Html()))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The page was not drawn in the expected state.");
            await Task.Delay(10);
        }
    }

    /// <summary>A graded sentence, as SubmitSentenceShortcut leaves it, then the page drawn again.</summary>
    private static async Task ShowGradedResultAsync(ActivityPageHarness harness, string target)
    {
        var resultType = typeof(VocabQuiz).GetNestedType("SentenceShortcutResult", BindingFlags.NonPublic)!;
        var result = Activator.CreateInstance(resultType,
            $"Graded: {target}", true, $"{target} is used correctly.", 90.0, 85.0, "", "", "", "")!;
        harness.Get<IList>("sentenceShortcutResults").Add(result);
        harness.Set("sentenceShortcutSuccessCount", 1);
        await harness.DispatchAsync("OnCultureChanged");
    }

    /// <summary>
    /// The quiz on the learner's own words, NativeToTarget, autoplay off unless asked. The page is
    /// created under the English UI culture (its localizer keeps the culture it is created with),
    /// so the English assertions hold on any machine, as the Coach tests pin it.
    /// </summary>
    private static async Task<ActivityPageHarness> OpenQuizAsync(
        bool canPlayLocally = true, bool autoPlay = false, Action<ActivityPageHarness>? beforeRender = null)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        try
        {
            var harness = await ActivityPageHarness.CreateAsync(AddQuizServices);
            harness.Audio.CanPlayLocally = canPlayLocally;
            var preferences = harness.Services.GetRequiredService<VocabularyQuizPreferences>();
            preferences.DisplayDirection = "NativeToTarget";
            preferences.AutoPlayVocabAudio = autoPlay;
            await harness.WithDbAsync(async db =>
            {
                var resource = new LearningResource
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = "Owner words",
                    Language = "Korean",
                    UserProfileId = ActivityPageHarness.OwnerA,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                db.LearningResources.Add(resource);
                foreach (var (target, native) in Words)
                {
                    var word = new VocabularyWord
                    {
                        Id = Guid.NewGuid().ToString(),
                        TargetLanguageTerm = target,
                        NativeLanguageTerm = native,
                        Language = "Korean",
                        LexicalUnitType = LexicalUnitType.Word,
                        UserProfileId = ActivityPageHarness.OwnerA,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    db.VocabularyWords.Add(word);
                    db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                    {
                        Id = Guid.NewGuid().ToString(), ResourceId = resource.Id, VocabularyWordId = word.Id
                    });
                }
                return await db.SaveChangesAsync();
            });
            beforeRender?.Invoke(harness);
            await harness.RenderAsync<VocabQuiz>();
            await harness.WaitUntilAsync(() => harness.Get<VocabularyQuizItem?>("currentItem") is not null);
            return harness;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>The quiz's services beyond the harness's shared set; grading AI is never called.</summary>
    private static void AddQuizServices(IServiceCollection services)
    {
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:OpenAIKey"] = "fixture-only" })
            .Build());
        services.AddHttpClient();
        services.AddSingleton<IChatClient>(ThrowingStub<IChatClient>.Create());
        services.AddSingleton<AiService>();
        services.AddSingleton<TeacherService>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<VocabQuizLaunchValidator>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<VocabularyLearningContextRepository>();
        services.AddSingleton<VocabularyProgressService>();
        services.AddSingleton<ProgressCacheService>();
        services.AddSingleton<IProgressService>(ThrowingStub<IProgressService>.Create());
        services.AddSingleton<IActivitySessionService, ActivitySessionService>();
        services.AddSingleton<VocabularyQuizPreferences>();
        services.AddSingleton(new PhotoViewerCoordinator(
            ThrowingStub<IPhotoViewerService>.Create(), NullLogger<PhotoViewerCoordinator>.Instance));
        services.AddScoped<ExampleSentenceRepository>();
        services.AddSingleton<LanguageSegmenterFactory>();
        services.AddSingleton<IActivityTimerService>(new LeasingTimer());
    }

    /// <summary>Grants the quiz its timer lease; the timer itself is not under test.</summary>
    private sealed class LeasingTimer : IActivityTimerService
    {
        public bool IsActive => false;
        public bool IsRunning => false;
        public TimeSpan ElapsedTime => TimeSpan.Zero;
        public string? CurrentActivityType => null;
        public string? CurrentActivityId => null;
        public event EventHandler? TimerStateChanged { add { } remove { } }
        public event EventHandler<TimeSpan>? TimerTick { add { } remove { } }
        public ActivityTimerLease StartSession(
            string activityType, string? activityId = null, string? resourceId = null, string? skillId = null)
            => new(Guid.NewGuid(), 1, ActivityPageHarness.OwnerA, activityType, activityId);
        public Task<ActivityTimerLease?> StartValidatedSessionAsync(
            ActivityTimerStartRequest request, CancellationToken ct = default)
            => Task.FromResult<ActivityTimerLease?>(
                new(Guid.NewGuid(), 1, request.UserId, request.ActivityType.ToString(), null));
        public void Pause() { }
        public bool Pause(ActivityTimerLease lease) => true;
        public void Resume() { }
        public bool Resume(ActivityTimerLease lease) => true;
        public TimeSpan StopSession() => TimeSpan.Zero;
        public TimeSpan StopSession(ActivityTimerLease lease) => TimeSpan.Zero;
        public void CancelSession() { }
        public bool CancelSession(ActivityTimerLease lease) => true;
    }
}
