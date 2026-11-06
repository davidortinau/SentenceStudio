using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Captain, 2026-11-06: "I expect to see it quickly, and then hear the audio if it's set to
/// autoplay." A turn is returned (and shown) before its prompt plays; the head then asks for
/// the turn's autoplay once, after the prompt is on screen (source VocabQuiz.razor starts
/// PlayWordAudio without awaiting it).
/// </summary>
public sealed class NativeQuizAutoPlayTests
{
    private static async Task<(NativeSliceFixture Fixture, NativeQuizPresenter Quiz, NativeQuizState Question)>
        OpenAsync(bool autoPlay = true, bool holdPlayback = false)
    {
        var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UseTextPrompt = true;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = autoPlay;
        fixture.Media.AudioAvailable = true;
        fixture.Media.HoldPlaybackCompletion = holdPlayback;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        var question = await quiz.OpenAsync(launch!).WaitAsync(TimeSpan.FromSeconds(10));
        return (fixture, quiz, question);
    }

    [Fact]
    public async Task OpeningTheQuiz_ReturnsTheQuestionBeforeItsAudioPlays()
    {
        // Playback that never finishes would hold an inline autoplay (and the screen) forever.
        var (fixture, quiz, question) = await OpenAsync(holdPlayback: true);
        using var _ = fixture;
        using var __ = quiz;

        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.NotNull(question.Prompt!.PromptText);
        Assert.True(question.AutoPlayPending);
        Assert.Empty(fixture.Media.Played);

        var playing = quiz.PlayAutoPromptAsync(question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Media.FinishPlayback(true));
        var heard = await playing.WaitAsync(TimeSpan.FromSeconds(5));

        var played = Assert.Single(fixture.Media.Played);
        Assert.StartsWith("target-", played.Text);
        Assert.False(heard.AutoPlayPending);
        Assert.Equal(NativePhase.Ready, heard.Phase);
        Assert.Null(heard.ErrorKey);
    }

    [Fact]
    public async Task AutoplayRunsOncePerTurn_AndNeverForAStaleTurn()
    {
        var (fixture, quiz, question) = await OpenAsync();
        using var _ = fixture;
        using var __ = quiz;

        var stale = await quiz.PlayAutoPromptAsync("stale-turn");
        Assert.True(stale.AutoPlayPending);
        Assert.Equal(question.TurnId, stale.TurnId);
        Assert.Empty(fixture.Media.Played);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Single(fixture.Media.Played);
    }

    [Fact]
    public async Task AutoplayOff_NeverAsksTheHeadToPlay()
    {
        var (fixture, quiz, question) = await OpenAsync(autoPlay: false);
        using var _ = fixture;
        using var __ = quiz;

        Assert.False(question.AutoPlayPending);
        Assert.True(question.Prompt!.CanPlayPromptAudio);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Empty(fixture.Media.Played);
    }

    [Fact]
    public async Task AnsweringOrPlayingByHandFirst_EndsTheTurnsAutoplay()
    {
        var (fixture, quiz, question) = await OpenAsync();
        using var _ = fixture;
        using var __ = quiz;

        var answered = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, answered.Phase);
        Assert.False(answered.AutoPlayPending);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Empty(fixture.Media.Played);

        var next = await quiz.NextAsync(answered.TurnId!);
        Assert.True(next.AutoPlayPending);
        await quiz.PlayPromptAsync();
        Assert.Single(fixture.Media.Played);
        await quiz.PlayAutoPromptAsync(next.TurnId!);
        Assert.Single(fixture.Media.Played);
    }

    [Fact]
    public async Task SentenceShortcut_NeverAutoplaysTheWord()
    {
        // NativeToTarget: the prompt is the native word, so playing it while the learner
        // writes with the target word would speak the translation.
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.True(question.AutoPlayPending);

        var sentence = quiz.OpenSentenceShortcut();
        Assert.NotNull(sentence.Sentence);
        Assert.False(sentence.AutoPlayPending);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Empty(fixture.Media.Played);

        var back = quiz.CancelSentenceShortcut();
        Assert.Null(back.Sentence);
        Assert.False(back.AutoPlayPending);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Empty(fixture.Media.Played);
    }

    [Fact]
    public async Task LearningDetailsOpenedDuringAutoplay_StayOpenOverTheHeardPrompt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = true;
        fixture.Media.AudioAvailable = true;
        fixture.Media.HoldPlaybackCompletion = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        // Audio-only target prompt: not answerable until the learner has heard it.
        Assert.False(question.CanSubmit);

        var playing = quiz.PlayAutoPromptAsync(question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativeQuizOverlay.LearningDetails, quiz.OpenDetails().Overlay);
        Assert.True(fixture.Media.FinishPlayback(true));
        var heard = await playing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(NativeQuizOverlay.LearningDetails, heard.Overlay);
        Assert.NotNull(heard.Details);
        Assert.True(heard.CanSubmit);
        var dismissed = quiz.DismissOverlay();
        Assert.Equal(NativeQuizOverlay.None, dismissed.Overlay);
        Assert.True(dismissed.CanSubmit);
    }

    [Fact]
    public async Task FailedAutoplay_ReportsTheConnectionAndKeepsTheQuestion()
    {
        var (fixture, quiz, question) = await OpenAsync();
        using var _ = fixture;
        using var __ = quiz;
        fixture.Media.PlaybackOffline = true;

        var failed = await quiz.PlayAutoPromptAsync(question.TurnId!);

        Assert.Equal(NativePhase.Ready, failed.Phase);
        Assert.Equal(NativeText.ConnectionError, failed.ErrorKey);
        Assert.False(failed.AutoPlayPending);
        Assert.Equal(question.TurnId, failed.TurnId);
        Assert.NotNull(failed.Prompt!.PromptText);
    }

    [Fact]
    public async Task AnAnswerWhileTheClipPlays_IsAccepted_AndNextStopsTheClipWithoutAnError()
    {
        // The gate is not held while the clip plays, so answering does not wait for it; Next
        // stops it on purpose, which is not a playback failure.
        var (fixture, quiz, question) = await OpenAsync(holdPlayback: true);
        using var _ = fixture;
        using var __ = quiz;

        var playing = quiz.PlayAutoPromptAsync(question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var answered = await quiz.AnswerAsync(question.Choices[0].Text)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativePhase.Feedback, answered.Phase);
        Assert.False(playing.IsCompleted);

        fixture.Media.HoldPlaybackCompletion = false;
        var next = await quiz.NextAsync(answered.TurnId!).WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await playing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.Null(next.ErrorKey);
        Assert.True(next.Prompt!.CanPlayPromptAudio);
        Assert.True(next.AutoPlayPending);
        // Stopped on purpose: whichever turn it returns, nothing is marked failed.
        Assert.Null(stopped.ErrorKey);
        Assert.Equal(next.TurnId, quiz.State.TurnId);
        Assert.Null(quiz.State.ErrorKey);
        Assert.True(quiz.State.Prompt!.CanPlayPromptAudio);
    }

    [Fact]
    public async Task PlayingByHandDuringAutoplay_StopsItWithoutMarkingAudioUnavailable()
    {
        var (fixture, quiz, question) = await OpenAsync(holdPlayback: true);
        using var _ = fixture;
        using var __ = quiz;

        var playing = quiz.PlayAutoPromptAsync(question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Media.HoldPlaybackCompletion = false;
        var manual = await quiz.PlayPromptAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var auto = await playing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, fixture.Media.Played.Count);
        Assert.Null(manual.ErrorKey);
        Assert.True(manual.Prompt!.CanPlayPromptAudio);
        Assert.Null(auto.ErrorKey);
        Assert.True(quiz.State.Prompt!.CanPlayPromptAudio);
        Assert.False(quiz.State.AutoPlayPending);
    }

    [Fact]
    public async Task APhotoPromptTurn_AutoplaysOnlyAfterItsPhotoIsDrawn()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = true;
        fixture.Media.AudioAvailable = true;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.True(question.Prompt!.PhotoRequested);
        Assert.False(question.Prompt.ShowPhoto);
        Assert.False(question.AutoPlayPending);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Empty(fixture.Media.Played);

        var drawn = await quiz.PhotoRenderedAsync(question.TurnId!);
        Assert.True(drawn.AutoPlayPending);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Single(fixture.Media.Played);
    }

    [Fact]
    public async Task AnOpenOverlay_HoldsAutoplayBackUntilItCloses()
    {
        var (fixture, quiz, question) = await OpenAsync();
        using var _ = fixture;
        using var __ = quiz;

        var details = quiz.OpenDetails();
        Assert.Equal(NativeQuizOverlay.LearningDetails, details.Overlay);
        Assert.False(details.AutoPlayPending);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Empty(fixture.Media.Played);

        var closed = quiz.DismissOverlay();
        Assert.True(closed.AutoPlayPending);
        await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Single(fixture.Media.Played);
    }
}
