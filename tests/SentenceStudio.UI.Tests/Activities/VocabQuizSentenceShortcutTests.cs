using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
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
/// translation. Opening it ends the prompt's playback as a deliberate stop: the turn keeps its
/// audio prompt and audio-answer mode, and a clip a previous turn requested never plays on the
/// next one. Runs the real page on a NativeToTarget turn.
/// </summary>
public sealed class VocabQuizSentenceShortcutTests
{
    private static readonly (string Target, string Native)[] Words =
        [("과일", "fruit"), ("사과", "apple"), ("학교", "school"), ("우유", "milk")];

    [Fact]
    public async Task TheCard_ShowsNoTranslation_BeforeOrAfterGrading()
    {
        await using var harness = await OpenQuizAsync();
        var word = harness.Get<VocabularyQuizItem>("currentItem").Word;
        harness.Html().Should().Contain(word.NativeLanguageTerm, "the NativeToTarget prompt is the native term");

        await harness.DispatchAsync("OpenSentenceShortcut");

        var card = harness.Html();
        AssertTargetOnly(card, word);
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
        harness.Js.HoldUntilStop = held;

        var play = harness.DispatchAsync("PlayWordAudio");
        await harness.Js.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DispatchAsync("OpenSentenceShortcut");
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Js.Calls.Select(call => call.Identifier).Should().Contain("audioInterop.stop");
        harness.Get<bool>("audioPromptAvailable").Should().Be(promptAudio, "the turn keeps its audio prompt");
        harness.Get<bool>("audioAnswerModeAvailable").Should().Be(answerAudio);
        harness.Get<bool>("isPlayingAudio").Should().BeFalse();
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        harness.Toasts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClipStillLoadingWhenTheShortcutOpens_NeverPlays(bool canPlayLocally)
    {
        await using var harness = await OpenQuizAsync(canPlayLocally);
        harness.Speech.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var play = harness.DispatchAsync("PlayWordAudio");
        await harness.Speech.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DispatchAsync("OpenSentenceShortcut");
        harness.Speech.Hold.SetResult();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        AssertNothingPlayed(harness);
        harness.Get<bool>("audioPromptAvailable").Should().BeTrue();
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        harness.Toasts.Should().BeEmpty();
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

    [Fact]
    public async Task NoPromptAudioStartsInTheShortcut()
    {
        await using var harness = await OpenQuizAsync();
        await harness.DispatchAsync("OpenSentenceShortcut");

        // An autoplay queued before the shortcut opened.
        await harness.DispatchAsync("PlayWordAudio");

        harness.Speech.Requests.Should().BeEmpty();
        harness.Audio.Players.Should().BeEmpty();
    }

    private static void AssertTargetOnly(string html, VocabularyWord word)
    {
        html.Should().Contain(word.TargetLanguageTerm);
        foreach (var (_, native) in Words)
            Regex.IsMatch(html, $@"\b{Regex.Escape(native)}\b", RegexOptions.IgnoreCase)
                .Should().BeFalse($"no translation (\"{native}\") is on the page while the learner writes");
    }

    private static void AssertNothingPlayed(ActivityPageHarness harness)
    {
        harness.Audio.Players.Should().BeEmpty();
        harness.Js.Calls.Should().NotContain(call => call.Identifier == "audioInterop.loadFromBase64"
            || call.Identifier == "audioInterop.startPlayback");
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

    /// <summary>The quiz on the learner's own words, NativeToTarget, autoplay off.</summary>
    private static async Task<ActivityPageHarness> OpenQuizAsync(bool canPlayLocally = true)
    {
        var harness = await ActivityPageHarness.CreateAsync(AddQuizServices);
        harness.Audio.CanPlayLocally = canPlayLocally;
        var preferences = harness.Services.GetRequiredService<VocabularyQuizPreferences>();
        preferences.DisplayDirection = "NativeToTarget";
        preferences.AutoPlayVocabAudio = false;
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
        await harness.RenderAsync<VocabQuiz>();
        await harness.WaitUntilAsync(() => harness.Get<VocabularyQuizItem?>("currentItem") is not null);
        return harness;
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
