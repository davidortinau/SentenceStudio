using SentenceStudio.Native;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidQuizFeedbackPhotoPolicyTests
{
    private static readonly NativeQuizPrompt WrittenPrompt = new(
        "owned-word", "target word", "Korean", null, null,
        IsTargetLanguagePrompt: true, ShowWrittenPrompt: true,
        CanPlayPromptAudio: false, ShowPhoto: false,
        TargetLanguageEngaged: true);

    private static NativeQuizState CorrectFeedback() => new(
        NativePhase.Feedback, NativeDestination.VocabularyQuiz,
        Prompt: WrittenPrompt, TurnId: "owned-turn",
        AnswerWasCorrect: true,
        FeedbackPhotoCandidateUri: "https://images.example.invalid/owned-photo");

    [Fact]
    public void CorrectFeedbackRequiresTheExactLoadedOwnerAndTurn()
    {
        var state = CorrectFeedback();

        Assert.True(AndroidQuizFeedbackPhotoPolicy.CanLoad(state, "owner-a"));
        Assert.True(AndroidQuizFeedbackPhotoPolicy.CanDisplay(state, "owner-a",
            "owner-a", "owned-turn", state.FeedbackPhotoCandidateUri));
        Assert.False(AndroidQuizFeedbackPhotoPolicy.CanDisplay(state, "owner-b",
            "owner-a", "owned-turn", state.FeedbackPhotoCandidateUri));
        Assert.False(AndroidQuizFeedbackPhotoPolicy.CanDisplay(state, "owner-a",
            "owner-a", "previous-turn", state.FeedbackPhotoCandidateUri));
        Assert.False(AndroidQuizFeedbackPhotoPolicy.CanDisplay(state, "owner-a",
            "owner-a", "owned-turn", "https://images.example.invalid/other"));
    }

    [Fact]
    public void UnverifiedOrDifferentTurnNeverExposesPhoto()
    {
        var state = CorrectFeedback();
        var invalid = new[]
        {
            state with { Phase = NativePhase.Ready },
            state with { Phase = NativePhase.Summary },
            state with { Destination = NativeDestination.TodaysPlan },
            state with { AnswerWasCorrect = false },
            state with { TurnId = null },
            state with { FeedbackPhotoCandidateUri = null },
            state with { Prompt = null },
            state with { Prompt = WrittenPrompt with { ShowPhoto = true } }
        };

        Assert.False(AndroidQuizFeedbackPhotoPolicy.CanLoad(state, null));
        Assert.False(AndroidQuizFeedbackPhotoPolicy.CanLoad(state, ""));
        foreach (var candidate in invalid)
        {
            Assert.False(AndroidQuizFeedbackPhotoPolicy.CanLoad(candidate, "owner-a"));
            Assert.False(AndroidQuizFeedbackPhotoPolicy.CanDisplay(candidate,
                "owner-a", "owner-a", "owned-turn",
                state.FeedbackPhotoCandidateUri));
        }
    }
}
