using SentenceStudio.Contracts.Coach;
using SentenceStudio.Native;
using Xunit;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// What the native heads tell Sam a screen shows. iOS and Android send the same description for
/// the same presenter state, so these cover both.
/// </summary>
public class NativeSamScreenTests
{
    private static NativeQuizPrompt Prompt(string wordId = "w-1", string text = "생선") =>
        new(wordId, text, "ko", text, null, IsTargetLanguagePrompt: true, ShowWrittenPrompt: true,
            CanPlayPromptAudio: true, ShowPhoto: false, TargetLanguageEngaged: true);

    [Fact]
    public void An_unanswered_quiz_names_the_word_and_hides_the_answer()
    {
        var shown = NativeSamScreen.Quiz(new NativeQuizState(NativePhase.Ready, NativeDestination.VocabularyQuiz,
            Prompt: Prompt(), Mode: "Text", CanSubmit: true));

        var item = Assert.Single(shown.Items);
        Assert.Equal(CoachClientScreenItemKinds.VocabularyWord, item.Kind);
        Assert.Equal("w-1", item.Id);
        Assert.Equal("생선", item.Label);
        Assert.Null(item.Detail);
        Assert.True(item.Focus);
        Assert.True(shown.AnswerHidden);
    }

    [Fact]
    public void A_graded_quiz_shows_the_answer_it_has_revealed()
    {
        var shown = NativeSamScreen.Quiz(new NativeQuizState(NativePhase.Feedback, NativeDestination.VocabularyQuiz,
            Prompt: Prompt(), Mode: "Text", CorrectAnswerAfterGrading: "fish", RequiresCorrection: true,
            AnswerWasCorrect: false));

        Assert.False(shown.AnswerHidden);
        Assert.Equal("fish", Assert.Single(shown.Items).Detail);
        Assert.Contains(("correctionRequired", "true"), shown.Facts!);
    }

    [Fact]
    public void A_prompt_the_screen_does_not_show_in_writing_is_not_named()
    {
        // An audio-only prompt: the learner hears the word; the screen does not print it.
        var audioOnly = Prompt() with { ShowWrittenPrompt = false };
        var shown = NativeSamScreen.Quiz(new NativeQuizState(NativePhase.Ready, NativeDestination.VocabularyQuiz,
            Prompt: audioOnly, CanSubmit: true));

        var item = Assert.Single(shown.Items);
        Assert.Equal("w-1", item.Id);
        Assert.Null(item.Label);
        Assert.True(shown.AnswerHidden);
    }

    [Fact]
    public void A_sentence_turn_names_the_word_and_hides_nothing()
    {
        var shown = NativeSamScreen.Quiz(new NativeQuizState(NativePhase.Ready, NativeDestination.VocabularyQuiz,
            Sentence: new NativeQuizSentencePrompt("w-2", "포도", "Korean")));

        Assert.False(shown.AnswerHidden);
        var item = Assert.Single(shown.Items);
        Assert.Equal(("w-2", "포도"), (item.Id, item.Label));
    }

    [Fact]
    public void Nothing_known_describes_nothing()
    {
        Assert.Empty(NativeSamScreen.Describe(null).Items);
        Assert.Empty(NativeSamScreen.Describe(new object()).Items);
    }

    [Fact]
    public void The_context_carries_the_description_and_drops_empty_facts()
    {
        var shown = new NativeSamScreenSnapshot(
            [new CoachClientScreenItem { Kind = CoachClientScreenItemKinds.VocabularyWord, Id = "w-1", Focus = true }],
            AnswerHidden: true,
            Facts: [("mode", "Text"), ("correctionRequired", null)]);

        var context = NativeSamScreenContext.Build("ios", "vocabulary-quiz", typeof(NativeSamScreenTests).Assembly,
            "iOS 26.0", "iPhone", "dark", debugBuild: true, [("tab", "0"), ("activityRoute", null)], shown);

        Assert.True(context.AnswerHidden);
        Assert.Equal("w-1", Assert.Single(context.Items!).Id);
        // Page facts go to Sam only, never into the state a feedback report stores.
        Assert.Contains(context.Facts!, entry => entry.Key == "mode" && entry.Value == "Text");
        Assert.DoesNotContain(context.Facts!, entry => entry.Key == "correctionRequired");
        Assert.DoesNotContain(context.State!, entry => entry.Key == "mode");
        // Shell state keeps a key with no value: it says there is no activity.
        Assert.Contains(context.State!, entry => entry.Key == "activityRoute" && entry.Value == null);
    }

    [Fact]
    public void An_activity_names_itself_and_gives_its_resource_the_launch_id()
    {
        // Reading shows its resource by title only; the launch knows which resource it is.
        var shown = new NativeSamScreenSnapshot(
        [
            new CoachClientScreenItem { Kind = CoachClientScreenItemKinds.LearningResource, Label = "Drama", Focus = true }
        ]);
        var launch = new NativeActivityLaunch(NativeActivityCatalog.Reading, "user", new DateOnly(2026, 11, 17),
            null, ["r-7"], "s-2", [], DueOnly: false, TimerResourceId: null);

        var items = NativeSamScreen.WithLaunch(shown, launch, "Reading").Items;

        Assert.Contains(items, i => i.Kind == CoachClientScreenItemKinds.LearningResource && i.Id == "r-7" && i.Label == "Drama" && i.Focus);
        Assert.Contains(items, i => i.Kind == CoachClientScreenItemKinds.Skill && i.Id == "s-2");
        var activity = Assert.Single(items, i => i.Kind == CoachClientScreenItemKinds.Activity);
        Assert.False(activity.Focus, "the resource on screen is the focus, not the activity");
    }

    [Fact]
    public void An_activity_with_nothing_on_screen_is_itself_the_focus()
    {
        var launch = new NativeActivityLaunch(NativeActivityCatalog.Writing, "user", new DateOnly(2026, 11, 17),
            null, [], null, [], DueOnly: false, TimerResourceId: null);

        var items = NativeSamScreen.WithLaunch(NativeSamScreenSnapshot.Empty, launch, "Writing").Items;

        Assert.True(Assert.Single(items).Focus);
    }
}
