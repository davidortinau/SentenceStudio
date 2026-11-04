using System.Collections.Immutable;
using SentenceStudio.Native;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidQuizTurnPolicyTests
{
    private static readonly NativeQuizPrompt NativePrompt = new(
        "owned-word", "dog", "English", "dog", null,
        IsTargetLanguagePrompt: false, ShowWrittenPrompt: true,
        CanPlayPromptAudio: true, ShowPhoto: false,
        TargetLanguageEngaged: true);

    private static readonly NativeQuizPrompt TargetPrompt = NativePrompt with
    {
        PromptText = "개", PromptLanguage = "Korean", IsTargetLanguagePrompt = true
    };

    private static NativeQuizState AudioChoices(bool canSubmit = true) => new(
        NativePhase.Ready, NativeDestination.VocabularyQuiz,
        Prompt: NativePrompt,
        Choices: ImmutableArray.Create(
            new NativeQuizChoice("개"), new NativeQuizChoice("고양이"),
            new NativeQuizChoice("새")),
        Mode: "MultipleChoice", CanSubmit: canSubmit, TurnId: "owned-turn")
    {
        CanPlayAnswerAudio = true
    };

    [Fact]
    public void NativePromptWithPlayableTargetAnswersUsesLetteredAudioOptions()
    {
        var state = AudioChoices();
        Assert.True(AndroidQuizTurnPolicy.UsesAudioChoices(state));
        Assert.Equal("A", AndroidQuizTurnPolicy.Letter(0));
        Assert.Equal("D", AndroidQuizTurnPolicy.Letter(3));

        Assert.False(AndroidQuizTurnPolicy.UsesAudioChoices(
            state with { CanPlayAnswerAudio = false }));
        Assert.False(AndroidQuizTurnPolicy.UsesAudioChoices(
            state with { Prompt = TargetPrompt }));
        Assert.False(AndroidQuizTurnPolicy.UsesAudioChoices(
            state with { Mode = "Text" }));
    }

    [Fact]
    public void LetteredAnswerFollowsThePresenterWithoutAHeadListenFirstGate()
    {
        // Captain 2026-11-03 (AF-Q03): no per-choice heard gate in the head.
        var state = AudioChoices();
        Assert.True(state.HeardAnswerChoiceIndices.IsEmpty);
        Assert.True(AndroidQuizTurnPolicy.CanChoose(state, busy: false));
        Assert.False(AndroidQuizTurnPolicy.CanChoose(state, busy: true));
        Assert.False(AndroidQuizTurnPolicy.CanChoose(AudioChoices(canSubmit: false), false));
        Assert.False(AndroidQuizTurnPolicy.CanChoose(
            state with { Phase = NativePhase.Feedback }, false));
        Assert.False(AndroidQuizTurnPolicy.CanChoose(
            state with { Overlay = NativeQuizOverlay.LearningDetails }, false));

        Assert.True(AndroidQuizTurnPolicy.CanPlayChoice(state, busy: false));
        Assert.True(AndroidQuizTurnPolicy.CanPlayChoice(
            state with { Phase = NativePhase.Feedback }, busy: false));
        Assert.False(AndroidQuizTurnPolicy.CanPlayChoice(state, busy: true));
        Assert.False(AndroidQuizTurnPolicy.CanPlayChoice(
            state with { CanPlayAnswerAudio = false }, busy: false));
    }

    [Fact]
    public void SentenceShortcutAndHideTextAreOfferedOnlyBeforeGrading()
    {
        var question = AudioChoices();
        Assert.True(AndroidQuizTurnPolicy.CanOpenSentence(question, busy: false));
        Assert.False(AndroidQuizTurnPolicy.CanOpenSentence(question, busy: true));
        Assert.False(AndroidQuizTurnPolicy.CanOpenSentence(
            question with { Phase = NativePhase.Feedback }, false));
        Assert.False(AndroidQuizTurnPolicy.CanOpenSentence(question with
        {
            Sentence = new NativeQuizSentencePrompt("owned-word", "개", "dog", "Korean")
        }, false));

        Assert.False(AndroidQuizTurnPolicy.CanTogglePhotoText(question, false));
        var eligible = question with
        {
            Prompt = NativePrompt with { CanTogglePhotoText = true }
        };
        Assert.True(AndroidQuizTurnPolicy.CanTogglePhotoText(eligible, false));
        Assert.False(AndroidQuizTurnPolicy.CanTogglePhotoText(
            eligible with { Phase = NativePhase.Feedback }, false));
    }

    [Fact]
    public void SampleSentencesAreOnlyForTargetLanguagePrompts()
    {
        var target = AudioChoices() with
        {
            Prompt = TargetPrompt, CanShowSentenceHints = true
        };
        Assert.True(AndroidQuizTurnPolicy.CanToggleSentenceHints(target, false));
        Assert.True(AndroidQuizTurnPolicy.CanToggleSentenceHints(
            target with { Phase = NativePhase.Feedback }, false));
        Assert.False(AndroidQuizTurnPolicy.CanToggleSentenceHints(
            target with { Prompt = NativePrompt }, false));
        Assert.False(AndroidQuizTurnPolicy.CanToggleSentenceHints(
            target with { CanShowSentenceHints = false }, false));
        Assert.False(AndroidQuizTurnPolicy.CanToggleSentenceHints(target, true));

        Assert.True(AndroidQuizTurnPolicy.CanOpenDetails(target, false));
        Assert.False(AndroidQuizTurnPolicy.CanOpenDetails(
            target with { Overlay = NativeQuizOverlay.FullscreenPhoto }, false));
    }

    [Fact]
    public void RetryRepeatsTheCommandWhoseSaveFailed()
    {
        var failed = AudioChoices() with
        {
            Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed,
            Mode = "Text", CanSubmit = false
        };
        Assert.Equal(AndroidQuizRetry.PendingSave, AndroidQuizTurnPolicy.Route(
            failed, AndroidQuizFailedCommand.Answer, hasLaunch: true));
        Assert.Equal(AndroidQuizRetry.Override, AndroidQuizTurnPolicy.Route(
            failed with { RequiresCorrection = true },
            AndroidQuizFailedCommand.Override, hasLaunch: true));
        Assert.Equal(AndroidQuizRetry.Correction, AndroidQuizTurnPolicy.Route(
            failed with { RequiresCorrection = true },
            AndroidQuizFailedCommand.Correction, hasLaunch: true));
        // A correction retry needs the correction still owed.
        Assert.Equal(AndroidQuizRetry.PendingSave, AndroidQuizTurnPolicy.Route(
            failed, AndroidQuizFailedCommand.Correction, hasLaunch: true));
        Assert.Equal(AndroidQuizRetry.Sentence, AndroidQuizTurnPolicy.Route(
            failed with
            {
                Sentence = new NativeQuizSentencePrompt("owned-word", "개", "dog", "Korean")
            }, AndroidQuizFailedCommand.None, hasLaunch: true));
        Assert.Equal(AndroidQuizRetry.Done, AndroidQuizTurnPolicy.Route(
            new NativeQuizState(NativePhase.Error, NativeDestination.QuizSummary,
                ErrorKey: NativeText.SaveFailed),
            AndroidQuizFailedCommand.None, hasLaunch: true));
    }

    [Fact]
    public void RetryIsOfferedOnlyWhereARetryCanSucceed()
    {
        var opening = new NativeQuizState(NativePhase.Error,
            NativeDestination.VocabularyQuiz, ErrorKey: NativeText.ConnectionError);
        Assert.Equal(AndroidQuizRetry.Reopen, AndroidQuizTurnPolicy.Route(
            opening, AndroidQuizFailedCommand.None, hasLaunch: true));
        Assert.Equal(AndroidQuizRetry.None, AndroidQuizTurnPolicy.Route(
            opening, AndroidQuizFailedCommand.None, hasLaunch: false));
        Assert.Equal(AndroidQuizRetry.None, AndroidQuizTurnPolicy.Route(
            AudioChoices(), AndroidQuizFailedCommand.Answer, hasLaunch: true));
        Assert.Equal(AndroidQuizRetry.None, AndroidQuizTurnPolicy.Route(
            opening with { Destination = NativeDestination.TodaysPlan },
            AndroidQuizFailedCommand.None, hasLaunch: true));
    }

    [Fact]
    public void BackLeavesWhenThePresenterRoutesToTodaysPlan()
    {
        Assert.True(AndroidQuizTurnPolicy.BackLeavesQuiz(new NativeQuizState(
            NativePhase.Ready, NativeDestination.TodaysPlan)));
        // Refused to overwrite a newer committed turn: the run resumes later.
        Assert.True(AndroidQuizTurnPolicy.BackLeavesQuiz(new NativeQuizState(
            NativePhase.Error, NativeDestination.TodaysPlan,
            ErrorKey: NativeText.SaveFailed)));
        Assert.True(AndroidQuizTurnPolicy.BackLeavesQuiz(new NativeQuizState(
            NativePhase.Blocked, NativeDestination.VocabularyQuiz)));
        // A failed suspension keeps the learner on the turn.
        Assert.False(AndroidQuizTurnPolicy.BackLeavesQuiz(AudioChoices() with
        {
            Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed
        }));
        Assert.False(AndroidQuizTurnPolicy.BackLeavesQuiz(AudioChoices()));
    }
}
