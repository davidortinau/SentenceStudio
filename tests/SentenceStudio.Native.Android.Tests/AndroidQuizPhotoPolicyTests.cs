using SentenceStudio.Native;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidQuizPhotoPolicyTests
{
    private static readonly NativeQuizPhotoAsset Asset =
        new([137, 80, 78, 71, 13, 10, 26, 10], "image/png");

    private static readonly NativeQuizPrompt RequestedPrompt = new(
        "owned-word", "target word", "Korean", null, null,
        IsTargetLanguagePrompt: true, ShowWrittenPrompt: true,
        CanPlayPromptAudio: false, ShowPhoto: false,
        TargetLanguageEngaged: true)
    {
        Photo = Asset,
        PhotoRequested = true,
        CanTogglePhoto = true,
    };

    private static NativeQuizState Question(NativeQuizPrompt? prompt = null) => new(
        NativePhase.Ready, NativeDestination.VocabularyQuiz,
        Prompt: prompt ?? RequestedPrompt, TurnId: "owned-turn");

    [Fact]
    public void RequestedPhotoIsDrawnAndReportedOnceForTheActiveOwner()
    {
        var state = Question();

        Assert.True(AndroidQuizPhotoPolicy.ShowsThumbnail(state));
        Assert.True(AndroidQuizPhotoPolicy.NeedsRenderReport(state, "owner-a"));
        Assert.False(AndroidQuizPhotoPolicy.NeedsRenderReport(state, null));
        Assert.False(AndroidQuizPhotoPolicy.NeedsRenderReport(state, " "));
        // Not yet counted as a prompt, so it cannot open full screen.
        Assert.False(AndroidQuizPhotoPolicy.CanOpenFullScreen(state));

        var shown = state with
        {
            Prompt = RequestedPrompt with
            {
                ShowPhoto = true,
                PhotoUri = "https://images.example.invalid/owned-photo"
            }
        };
        Assert.True(AndroidQuizPhotoPolicy.ShowsThumbnail(shown));
        Assert.False(AndroidQuizPhotoPolicy.NeedsRenderReport(shown, "owner-a"));
        Assert.True(AndroidQuizPhotoPolicy.CanOpenFullScreen(shown));
    }

    [Fact]
    public void CorrectFeedbackRevealsTheMnemonicImageFullScreen()
    {
        // Photo prompt off: the presenter requests the image only for correct feedback.
        var feedback = new NativeQuizState(
            NativePhase.Feedback, NativeDestination.VocabularyQuiz,
            Prompt: RequestedPrompt with
            {
                CanTogglePhoto = false,
                ShowPhoto = true,
                PhotoUri = "https://images.example.invalid/owned-photo"
            },
            TurnId: "owned-turn", AnswerWasCorrect: true);

        Assert.True(AndroidQuizPhotoPolicy.ShowsThumbnail(feedback));
        Assert.True(AndroidQuizPhotoPolicy.CanOpenFullScreen(feedback));
        Assert.True(AndroidQuizPhotoPolicy.ShowsToggle(feedback));
    }

    [Fact]
    public void UnloadedUnrequestedOrOtherScreensNeverShowAPhoto()
    {
        var state = Question();
        NativeQuizState[] hidden =
        [
            state with { Prompt = RequestedPrompt with { Photo = null } },
            state with { Prompt = RequestedPrompt with { Photo = new([], "image/png") } },
            state with { Prompt = RequestedPrompt with { PhotoRequested = false } },
            state with { Prompt = null },
            state with { TurnId = null },
            state with { Phase = NativePhase.Loading },
            state with { Phase = NativePhase.Summary },
            state with { Phase = NativePhase.Error },
            state with { Destination = NativeDestination.TodaysPlan },
            state with { Sentence = new NativeQuizSentencePrompt(
                "owned-word", "target", "gloss", "Korean") },
        ];
        foreach (var candidate in hidden)
        {
            Assert.False(AndroidQuizPhotoPolicy.ShowsThumbnail(candidate));
            Assert.False(AndroidQuizPhotoPolicy.NeedsRenderReport(candidate, "owner-a"));
            Assert.False(AndroidQuizPhotoPolicy.CanOpenFullScreen(candidate));
        }
        // An open overlay waits: the report is for the visible question only.
        Assert.False(AndroidQuizPhotoPolicy.NeedsRenderReport(
            state with { Overlay = NativeQuizOverlay.LearningDetails }, "owner-a"));
    }

    [Fact]
    public void ShowImageStaysAvailableAfterTheLearnerHidesThePhoto()
    {
        var hiddenByLearner = Question(RequestedPrompt with { PhotoRequested = false });
        Assert.False(AndroidQuizPhotoPolicy.ShowsThumbnail(hiddenByLearner));
        Assert.True(AndroidQuizPhotoPolicy.ShowsToggle(hiddenByLearner));

        Assert.False(AndroidQuizPhotoPolicy.ShowsToggle(Question(RequestedPrompt with
        {
            PhotoRequested = false, CanTogglePhoto = false
        })));
        Assert.False(AndroidQuizPhotoPolicy.ShowsToggle(Question(RequestedPrompt with
        {
            Photo = null
        })));
    }
}
