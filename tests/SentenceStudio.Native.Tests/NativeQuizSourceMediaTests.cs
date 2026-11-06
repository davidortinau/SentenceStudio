using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizSourceMediaTests
{
    [Fact]
    public async Task TargetPrompt_ProjectsOwnedTargetOnlyHintsAndRealPhotoWithoutAnswerLeak()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        await SeedHintsAsync(fixture);
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = true;
        fixture.Media.AudioAvailable = true;
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);

        var awaitingImage = await quiz.OpenAsync(launch!);
        Assert.True(awaitingImage.Prompt!.PhotoRequested);
        Assert.False(awaitingImage.Prompt.ShowPhoto);
        Assert.True(awaitingImage.Prompt.ShowWrittenPrompt);
        var question = await quiz.PhotoRenderedAsync(awaitingImage.TurnId!);
        Assert.Empty(fixture.Media.Played);
        // The head plays the turn's autoplay once the prompt is on screen.
        question = await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.True(question.Prompt!.ShowPhoto);
        Assert.NotNull(question.Prompt.Photo);
        Assert.Equal(NativeText.QuizPhoto, question.Prompt.PhotoAccessibilityKey);
        Assert.Equal("image/png", question.Prompt.Photo.ContentType);
        Assert.False(question.Prompt.CanTogglePhotoText);
        Assert.True(question.CanShowSentenceHints);
        Assert.False(question.SentenceHintsExpanded);
        Assert.Empty(question.SentenceHints);
        Assert.Equal(
            "Korean", fixture.Media.Played[0].Language);
        Assert.StartsWith("target-", fixture.Media.Played[0].Text);

        var withHints = quiz.ToggleSentenceHints();
        Assert.True(withHints.SentenceHintsExpanded);
        Assert.Single(withHints.SentenceHints);
        Assert.StartsWith("target-language example", withHints.SentenceHints[0].TargetSentence);
        Assert.DoesNotContain("must never", withHints.SentenceHints[0].TargetSentence);
        var details = quiz.OpenDetails().Details;
        Assert.NotNull(details);
        Assert.DoesNotContain(question.Prompt.PromptText!,
            System.Text.Json.JsonSerializer.Serialize(details));
        quiz.DismissOverlay();
        var photo = quiz.OpenPhoto();
        Assert.Equal(NativeQuizOverlay.FullscreenPhoto, photo.Overlay);
        Assert.False(photo.SentenceHintsExpanded);
        quiz.DismissOverlay();

        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.NotNull(feedback.FeedbackStartedAt);
        Assert.Equal(2000, feedback.AutoAdvanceDurationMs);
    }

    [Fact]
    public async Task HiddenPhoto_KeepsItsToggleSoTheLearnerCanShowItAgain()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = true;
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        var opened = await quiz.OpenAsync(launch!);
        var shown = await quiz.PhotoRenderedAsync(opened.TurnId!);
        Assert.True(shown.Prompt!.ShowPhoto);
        Assert.True(shown.Prompt.CanTogglePhoto);

        var hidden = await quiz.SetPhotoVisibleAsync(false, shown.TurnId!);
        Assert.False(hidden.Prompt!.ShowPhoto);
        Assert.False(hidden.Prompt.PhotoRequested);
        Assert.True(hidden.Prompt.CanTogglePhoto);

        var requested = await quiz.SetPhotoVisibleAsync(true, hidden.TurnId!);
        Assert.True(requested.Prompt!.PhotoRequested);
        var again = await quiz.PhotoRenderedAsync(requested.TurnId!);
        Assert.True(again.Prompt!.ShowPhoto);
    }

    [Fact]
    public async Task PhotoToggle_IsUnavailableWhenThePhotoModalityIsOff()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UsePhotoPrompt = false;
        fixture.QuizPreferences.ShowMnemonicImage = true;
        fixture.Media.PhotoAvailable = true;
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        var opened = await quiz.OpenAsync(launch!);

        Assert.False(opened.Prompt!.CanTogglePhoto);
    }

    [Fact]
    public async Task NativePhotoPrompt_OnlyOwnedTogglePersistsAndAnswerAudioDoesNotSelect()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        await SeedHintsAsync(fixture);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = true;
        fixture.Media.AudioAvailable = true;
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        var awaitingImage = await quiz.OpenAsync(launch!);
        Assert.True(awaitingImage.Prompt!.PhotoRequested);
        Assert.True(awaitingImage.Prompt.ShowWrittenPrompt);
        var question = await quiz.PhotoRenderedAsync(awaitingImage.TurnId!);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Null(question.Prompt!.PromptText);
        Assert.True(question.Prompt.CanTogglePhotoText);
        // AF-Q03 (Captain, 2026-11-03): audio answer letters can be picked at once.
        Assert.True(question.Prompt.TargetLanguageEngaged);
        Assert.True(question.CanSubmit);
        Assert.False(question.CanShowSentenceHints);
        Assert.True(question.CanPlayAnswerAudio);
        var played = await quiz.PlayAnswerChoiceAsync(1, question.TurnId!);
        Assert.Equal(NativePhase.Ready, played.Phase);
        Assert.True(played.CanSubmit);
        Assert.Equal(1, Assert.Single(played.HeardAnswerChoiceIndices));
        Assert.Null(played.SelectedAnswer);
        Assert.Equal(question.Choices[1].Text, fixture.Media.Played[^1].Text);
        Assert.Equal("Korean", fixture.Media.Played[^1].Language);
        Assert.Equal(0, played.TotalTurns);

        var shown = await quiz.SetPhotoTextVisibleAsync(true, question.TurnId!);
        Assert.True(shown.Prompt!.ShowTextWithPhoto);
        Assert.StartsWith("native-", shown.Prompt.PromptText);
        Assert.True(shown.HasPendingSync);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True((await db.UserProfiles.AsNoTracking()
                .SingleAsync(profile => profile.Id == NativeSliceFixture.UserA))
                .VocabQuizShowTextWithPhoto);
            Assert.False((await db.UserProfiles.AsNoTracking()
                .SingleAsync(profile => profile.Id == NativeSliceFixture.UserB))
                .VocabQuizShowTextWithPhoto);
        }
        var hidden = await quiz.SetPhotoTextVisibleAsync(false, question.TurnId!);
        Assert.Null(hidden.Prompt!.PromptText);
        // Choice 0 was never played; it is still accepted, like the source.
        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.Equal(1, feedback.TotalTurns);
        Assert.NotNull(feedback.Prompt!.PromptText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReversedPhotoFeedback_RevealsNativeCueForEitherVerdictThenResets(
        bool correctAnswer)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = true;
        fixture.QuizPreferences.UseAudioPrompt = false;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = true;
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        var initial = await quiz.OpenAsync(launch!);
        var question = await quiz.PhotoRenderedAsync(initial.TurnId!);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.True(question.Prompt!.ShowPhoto);
        Assert.Null(question.Prompt.PromptText);
        Assert.Null(question.CorrectAnswerAfterGrading);
        using var scope = fixture.Scope();
        var word = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking()
            .SingleAsync(item => item.Id == question.Prompt.WordId);
        var option = question.Choices.First(choice =>
            string.Equals(choice.Text, word.TargetLanguageTerm,
                StringComparison.Ordinal) == correctAnswer);

        var feedback = await quiz.AnswerAsync(option.Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.Equal(correctAnswer
            ? NativeText.QuizCorrect : NativeText.QuizIncorrect,
            feedback.FeedbackKey);
        Assert.Equal(word.TargetLanguageTerm,
            feedback.CorrectAnswerAfterGrading);
        Assert.Equal(word.NativeLanguageTerm, feedback.Prompt!.PromptText);
        Assert.True(feedback.Prompt.ShowPhoto);
        Assert.Equal(correctAnswer, feedback.AnswerWasCorrect);
        Assert.Null(feedback.Sentence);

        var next = await quiz.NextAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.Null(next.FeedbackKey);
        Assert.Null(next.CorrectAnswerAfterGrading);
        Assert.Null(next.AnswerWasCorrect);
        var nextRendered = await quiz.PhotoRenderedAsync(next.TurnId!);
        Assert.Null(nextRendered.Prompt!.PromptText);
    }

    [Fact]
    public async Task MissingMedia_UsesWrittenL2PromptWithoutClaimingPlaybackOrPhoto()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        var question = await quiz.OpenAsync(launch!);

        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.True(question.Prompt!.ShowWrittenPrompt);
        Assert.True(question.Prompt.TargetLanguageEngaged);
        Assert.StartsWith("target-", question.Prompt.PromptText);
        Assert.False(question.Prompt.CanPlayPromptAudio);
        Assert.False(question.Prompt.ShowPhoto);
        Assert.Null(question.Prompt.Photo);
    }

    [Fact]
    public async Task FeedbackClock_UsesActualOnsetAndPhotoOverlayCancelsStaleAutoAdvance()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.AutoAdvanceDuration = 1000;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var awaitingImage = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var question = await quiz.PhotoRenderedAsync(awaitingImage.TurnId!);
        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.NotNull(feedback.FeedbackStartedAt);
        Assert.Equal(1000, feedback.AutoAdvanceDurationMs);
        var timer = quiz.AutoAdvanceAsync();
        Assert.Equal(NativeQuizOverlay.FullscreenPhoto, quiz.OpenPhoto().Overlay);
        var timed = await timer;
        Assert.Equal(NativePhase.Feedback, timed.Phase);
        Assert.Equal(NativeQuizOverlay.FullscreenPhoto, timed.Overlay);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        quiz.DismissOverlay();
        Assert.True((await quiz.NextAsync()).Phase is NativePhase.Ready or NativePhase.Summary);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task FailedAnswerAudio_ExposesTargetTextRatherThanGuessableLettersOrFalseSync()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        // The head plays the turn's autoplay once the prompt is on screen.
        question = await quiz.PlayAutoPromptAsync(question.TurnId!);
        Assert.True(question.CanPlayAnswerAudio);
        Assert.True(question.CanSubmit);
        Assert.False(question.HasPendingSync);
        fixture.Media.PlaySucceeds = false;

        var fallback = await quiz.PlayAnswerChoiceAsync(0, question.TurnId!);
        Assert.False(fallback.CanPlayAnswerAudio);
        Assert.Empty(fallback.HeardAnswerChoiceIndices);
        Assert.True(fallback.Prompt!.ShowWrittenPrompt);
        Assert.True(fallback.CanSubmit);
        Assert.Equal(NativeText.ConnectionError, fallback.ErrorKey);
        Assert.False(fallback.HasPendingSync);
        Assert.Equal(NativePhase.Feedback,
            (await quiz.AnswerAsync(fallback.Choices[0].Text)).Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativePromptAudioChoices_CanBePickedAtOnceAndRevealTheTargetForm(
        bool audioOn)
    {
        // AF-Q03 / Learning Value Gate: native prompt (written, default), target-language
        // options. With audio on, the options are letters with optional play buttons and any
        // letter can be picked before any audio; feedback then shows the target form.
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseAudioPrompt = audioOn;
        fixture.QuizPreferences.AutoPlayVocabAudio = false;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);

        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Equal("MultipleChoice", question.Mode);
        Assert.False(question.Prompt!.IsTargetLanguagePrompt);
        Assert.True(question.Prompt.ShowWrittenPrompt);
        Assert.Equal(audioOn, question.CanPlayAnswerAudio);
        Assert.Empty(question.HeardAnswerChoiceIndices);
        Assert.Empty(fixture.Media.Played);
        Assert.True(question.Prompt.TargetLanguageEngaged);
        Assert.True(question.CanSubmit);

        var unheard = question.Choices[^1].Text;
        var feedback = await quiz.AnswerAsync(unheard);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.Null(feedback.ErrorKey);
        Assert.Equal(1, feedback.TotalTurns);
        Assert.StartsWith("target-", feedback.CorrectAnswerAfterGrading);
        Assert.Contains(feedback.Choices, choice =>
            choice.Text == feedback.CorrectAnswerAfterGrading);
        Assert.Empty(fixture.Media.Played);
    }

    [Fact]
    public async Task AudioChoice_MarksHeardOnlyOnCompletionWithoutGatingSelection()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        // The head plays the turn's autoplay once the prompt is on screen.
        question = await quiz.PlayAutoPromptAsync(question.TurnId!);
        fixture.Media.HoldPlaybackCompletion = true;
        var stateChanges = 0;
        quiz.StateChanged += _ => Interlocked.Increment(ref stateChanges);

        var playing = quiz.PlayAnswerChoiceAsync(1, question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(playing.IsCompleted);
        Assert.Empty(quiz.State.HeardAnswerChoiceIndices);
        Assert.True(quiz.State.CanSubmit);

        Assert.True(fixture.Media.FinishPlayback(true));
        Assert.False(fixture.Media.FinishPlayback(true));
        var finished = await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Assert.Single(finished.HeardAnswerChoiceIndices));
        Assert.True(finished.CanSubmit);
        Assert.Equal(1, stateChanges);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetPromptAudio_RequiresCompletedPlaybackOrWrittenTargetFallback(
        bool completed)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = false;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.True(question.Prompt!.CanPlayPromptAudio);
        Assert.False(question.Prompt.ShowWrittenPrompt);
        Assert.False(question.CanSubmit);
        fixture.Media.HoldPlaybackCompletion = true;

        var playing = quiz.PlayPromptAsync();
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(playing.IsCompleted);
        Assert.False(quiz.State.CanSubmit);
        Assert.True(fixture.Media.FinishPlayback(completed));

        var result = await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(completed, result.Prompt!.CanPlayPromptAudio);
        Assert.Equal(!completed, result.Prompt.ShowWrittenPrompt);
        Assert.True(result.Prompt.TargetLanguageEngaged);
        Assert.True(result.CanSubmit);
        Assert.Equal(completed ? null : NativeText.ConnectionError, result.ErrorKey);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("decode")]
    [InlineData("interruption")]
    [InlineData("stop")]
    public async Task AudioChoice_StartFailureOrInterruptedPlayback_ShowsTargetText(
        string failure)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);

        if (failure == "start")
            fixture.Media.PlaySucceeds = false;
        else
            fixture.Media.HoldPlaybackCompletion = true;
        var playing = quiz.PlayAnswerChoiceAsync(0, question.TurnId!);
        if (failure != "start")
        {
            await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(playing.IsCompleted);
            if (failure == "stop")
                fixture.Media.StopPlayback();
            else
                Assert.True(fixture.Media.FinishPlayback(false));
        }
        var fallback = await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativePhase.Ready, fallback.Phase);
        Assert.False(fallback.CanPlayAnswerAudio);
        Assert.Empty(fallback.HeardAnswerChoiceIndices);
        Assert.True(fallback.Prompt!.ShowWrittenPrompt);
        Assert.True(fallback.Prompt.TargetLanguageEngaged);
        Assert.True(fallback.CanSubmit);
        Assert.Equal(NativeText.ConnectionError, fallback.ErrorKey);
        Assert.False(fallback.HasPendingSync);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task BackWhileChoicePlaying_StopsBeforePresenterGateAndIgnoresLateSuccess()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        fixture.Media.HoldPlaybackCompletion = true;
        var playing = quiz.PlayAnswerChoiceAsync(0, question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var staleCompletion = fixture.Media.PendingPlayback!;
        var stopsBeforeBack = fixture.Media.Stopped;

        var leaving = quiz.BackAsync();
        var plan = await leaving.WaitAsync(TimeSpan.FromSeconds(5));
        await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Media.Stopped > stopsBeforeBack);
        Assert.False(staleCompletion.TrySetResult(true));
        Assert.Equal(NativeDestination.TodaysPlan, plan.Destination);
        Assert.Equal(NativeDestination.TodaysPlan, quiz.State.Destination);
        Assert.Empty(quiz.State.HeardAnswerChoiceIndices);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task NextWhileFeedbackAudioPlaying_DoesNotCarryOldChoiceIntoNextTurn()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        // The head plays the turn's autoplay once the prompt is on screen.
        question = await quiz.PlayAutoPromptAsync(question.TurnId!);
        await quiz.PlayAnswerChoiceAsync(0, question.TurnId!);
        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        fixture.Media.HoldPlaybackCompletion = true;
        var playing = quiz.PlayAnswerChoiceAsync(1, feedback.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var staleCompletion = fixture.Media.PendingPlayback!;
        fixture.Media.HoldPlaybackCompletion = false;

        var advancing = quiz.NextAsync();
        var next = await advancing.WaitAsync(TimeSpan.FromSeconds(5));
        await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(staleCompletion.TrySetResult(true));
        Assert.True(next.Phase is NativePhase.Ready or NativePhase.Summary);
        Assert.Empty(next.HeardAnswerChoiceIndices);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task CancelledChoicePlayback_CannotMarkChoiceHeard()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        // The head plays the turn's autoplay once the prompt is on screen.
        question = await quiz.PlayAutoPromptAsync(question.TurnId!);
        fixture.Media.HoldPlaybackCompletion = true;
        using var cancellation = new CancellationTokenSource();
        var playing = quiz.PlayAnswerChoiceAsync(
            0, question.TurnId!, cancellation.Token);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await playing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(quiz.State.HeardAnswerChoiceIndices);
        Assert.True(quiz.State.CanSubmit);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task DisposingQuizDuringPlayback_CancelsWithoutReleasingDisposedGate()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        fixture.Media.HoldPlaybackCompletion = true;
        var playing = quiz.PlayAnswerChoiceAsync(0, question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var staleCompletion = fixture.Media.PendingPlayback!;

        quiz.Dispose();
        var afterDispose = await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(staleCompletion.TrySetResult(true));
        Assert.Equal(NativePhase.Idle, afterDispose.Phase);
        Assert.Empty(afterDispose.HeardAnswerChoiceIndices);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task DisposingQuizDuringAutoplay_CancelsWithoutAnotherWrite()
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
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);

        // The question opens without waiting for its audio; the head plays it once shown.
        var question = await quiz.OpenAsync(launch!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(question.AutoPlayPending);
        var playing = quiz.PlayAutoPromptAsync(question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var staleCompletion = fixture.Media.PendingPlayback!;
        quiz.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await playing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(staleCompletion.TrySetResult(true));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task OwnerChangesBeforeAudioCompletion_CannotMarkOldChoiceHeard()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        fixture.Media.HoldPlaybackCompletion = true;
        var playing = quiz.PlayAnswerChoiceAsync(0, question.TurnId!);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.True(fixture.Media.FinishPlayback(true));
        var denied = await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativePhase.Blocked, denied.Phase);
        Assert.Empty(denied.HeardAnswerChoiceIndices);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task StalePhotoCallback_DoesNotHideNativeWrittenFallbackOrStartFullscreen()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.QuizPreferences.UseAudioPrompt = false;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.True(question.Prompt!.PhotoRequested);
        Assert.False(question.Prompt.ShowPhoto);
        Assert.NotNull(question.Prompt.PromptText);
        var stale = await quiz.PhotoRenderedAsync("stale-turn");
        Assert.False(stale.Prompt!.ShowPhoto);
        Assert.Equal(NativeQuizOverlay.None, quiz.OpenPhoto().Overlay);
        var rendered = await quiz.PhotoRenderedAsync(question.TurnId!);
        Assert.True(rendered.Prompt!.ShowPhoto);
        Assert.Null(rendered.Prompt.PromptText);
        var failed = await quiz.PhotoFailedAsync(question.TurnId!);
        Assert.False(failed.Prompt!.ShowPhoto);
        Assert.NotNull(failed.Prompt.PromptText);
        Assert.True(failed.CanSubmit);
    }

    [Fact]
    public async Task RootResolvedQuiz_WithValidateScopes_QueriesOwnedHintsThroughScopedRepository()
    {
        using var fixture = new NativeSliceFixture(validateScopes: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        await SeedHintsAsync(fixture);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);

        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(itemId))!);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.True(question.CanShowSentenceHints);
        var hints = quiz.ToggleSentenceHints().SentenceHints;
        Assert.Single(hints);
        Assert.StartsWith("target-language example", hints[0].TargetSentence);
        Assert.DoesNotContain("unowned", hints[0].TargetSentence);
    }

    private static async Task SeedHintsAsync(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (var index = 0; index < 4; index++)
        {
            db.ExampleSentences.Add(new ExampleSentence
            {
                VocabularyWordId = fixture.WordId(NativeSliceFixture.UserA, index),
                LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                // The learner's own sentence: hints show only those (the one ownership rule).
                UserProfileId = NativeSliceFixture.UserA,
                TargetSentence = $"target-language example {index}",
                NativeSentence = "must never be projected",
                Status = ExampleSentenceStatus.Verified,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.ExampleSentences.Add(new ExampleSentence
            {
                VocabularyWordId = fixture.WordId(NativeSliceFixture.UserA, index),
                LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                UserProfileId = NativeSliceFixture.UserB,
                TargetSentence = "unowned and never projected",
                Status = ExampleSentenceStatus.Verified,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }
}
