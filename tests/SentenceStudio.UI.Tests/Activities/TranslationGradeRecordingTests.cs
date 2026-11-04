using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using TranslationPage = SentenceStudio.WebUI.Pages.Translation;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backports B9 (a failed Translation grade was saved as a 0% attempt), the Translation half of
/// B33 (Previous/Next stayed available while a grade was in flight, so a late result could land
/// on another sentence) and B25 (the activity row is written in UTC for its owner).
/// </summary>
public sealed class TranslationGradeRecordingTests
{
    private const string GradeFailed = "Error grading your translation. Please try again.";

    private static GradeResponse Grade(double accuracy, params string[] usedWords) => new()
    {
        Accuracy = accuracy,
        Fluency = 60,
        VocabularyAnalysis = usedWords.Select(word => new VocabularyAnalysis
        {
            DictionaryForm = word,
            UsedForm = word,
            UsageCorrect = true
        }).ToList()
    };

    private static TranslationResponse Sentences() => new()
    {
        Sentences =
        [
            new TranslationDto { SentenceText = "바다가 파래요.", RecommendedTranslation = "The sea is blue.", TranslationVocabulary = ["바다", "파래요"] },
            new TranslationDto { SentenceText = "바다에 가요.", RecommendedTranslation = "I go to the sea.", TranslationVocabulary = ["바다", "가요"] },
        ]
    };

    [Fact]
    public async Task Grade_AiFails_ShowsGradeFailedAndRecordsNothing()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithSentencesAsync(fixture);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");
        await SetInputAsync(fixture, page, "The sea is blue.");

        await fixture.Renderer.InvokeAsync(page, "GradeMe");

        fixture.Toasts.Should().Contain(t => t.Text == GradeFailed && t.Type == ToastType.Error);
        Members.Get<bool>(page, "showFeedback").Should().BeFalse("a failed grade is not a 0% result");
        Members.Get<int>(page, "sessionGradedCount").Should().Be(0);
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty("a failed grade is not a 0% attempt");
        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Grade_AiReturnsNoGrade_ShowsGradeFailedAndRecordsNothing()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithSentencesAsync(fixture);
        fixture.Ai.Respond = _ => null;
        await SetInputAsync(fixture, page, "The sea is blue.");

        await fixture.Renderer.InvokeAsync(page, "GradeMe");

        fixture.Toasts.Should().Contain(t => t.Text == GradeFailed && t.Type == ToastType.Error);
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Grade_RecordsTheSubmittedTextForTheOwnerInUtc_AndScoresOnlyOwnedWords()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithSentencesAsync(fixture);
        fixture.Ai.Respond = _ => Grade(88, "바다", "파래요");
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "The sea is blue.");
        var before = DateTime.UtcNow.AddSeconds(-1);

        var grading = fixture.Renderer.InvokeAsync(page, "GradeMe");
        await fixture.Ai.Started;
        await SetInputAsync(fixture, page, "typed while grading");
        await fixture.Renderer.InvokeAsync(page, "GradeMe");   // Enter again while grading
        fixture.Ai.Release();
        await grading;

        fixture.Ai.Prompts.Count(p => p.StartsWith("GradeTranslation")).Should().Be(1,
            "a second submit while grading is ignored");
        var activity = (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle().Subject;
        activity.Input.Should().Be("The sea is blue.");
        activity.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        activity.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(1),
            "streaks read UserActivity.CreatedAt as a UTC instant");
        var progress = (await fixture.RowsAsync<VocabularyProgress>()).Should().ContainSingle(
            "파래요 is a block the AI suggested, not a saved word, so only 바다 is scored").Subject;
        progress.UserId.Should().Be(ActivityPageFixture.OwnerA);
        fixture.Toasts.Should().Contain(t => t.Text == "Graded! Accuracy: 88%" && t.Type == ToastType.Success);
    }

    [Fact]
    public async Task Grade_InFlight_NextAndPreviousDoNotMove_SoTheGradeLandsOnItsSentence()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithSentencesAsync(fixture);
        fixture.Ai.Respond = _ => Grade(70);
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "The sea is blue.");

        var grading = fixture.Renderer.InvokeAsync(page, "GradeMe");
        await fixture.Ai.Started;
        await fixture.Renderer.InvokeAsync(page, "NextSentence");
        Members.Get<int>(page, "currentIndex").Should().Be(0, "the sentence cannot change while it is being graded");
        fixture.Ai.Release();
        await grading;

        Members.Get<int>(page, "currentIndex").Should().Be(0);
        Members.Get<bool>(page, "showFeedback").Should().BeTrue();
        Members.Get<string>(page, "lastSubmittedInput").Should().Be("The sea is blue.");
        (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle();

        await fixture.Renderer.InvokeAsync(page, "NextSentence");
        Members.Get<int>(page, "currentIndex").Should().Be(1, "after the grade lands the learner moves on");
        Members.Get<bool>(page, "showFeedback").Should().BeFalse();
    }

    [Fact]
    public async Task Grade_InFlightWhenTheLearnerLeaves_RecordsNothing()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithSentencesAsync(fixture);
        fixture.Ai.Respond = _ => Grade(95);
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "The sea is blue.");

        var grading = fixture.Renderer.InvokeAsync(page, "GradeMe");
        await fixture.Ai.Started;
        await fixture.Renderer.InvokeAsync(page, "GoBack");
        fixture.Ai.Release();
        await grading;

        fixture.Navigation.Visited.Should().ContainSingle();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
        Members.Get<bool>(page, "showFeedback").Should().BeFalse();
        fixture.Toasts.Should().NotContain(t => t.Type == ToastType.Success);
    }

    [Fact]
    public async Task NoActiveProfile_LoadsNothingAndGradesNothing()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        fixture.Preferences.ActiveId = string.Empty;
        var (_, page) = await fixture.Renderer.RenderAsync<TranslationPage>();
        fixture.Toasts.Should().Contain(t => t.Text == "No active user profile. Please select a profile first.");

        await fixture.Renderer.InvokeAsync(page, "GradeMe");

        fixture.Ai.Prompts.Should().BeEmpty();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
    }

    /// <summary>Opens the page on the owner's focus word, so the real load generates two sentences.</summary>
    private static async Task<TranslationPage> OpenWithSentencesAsync(ActivityPageFixture fixture)
    {
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var (_, page) = await fixture.Renderer.RenderAsync<TranslationPage>();
        fixture.Ai.Respond = type => type == typeof(TranslationResponse) ? Sentences() : null;
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            Members.Set(page, "FocusVocabularyIdsParam", words[0].Id));
        await fixture.Renderer.InvokeAsync(page, "LoadSentences");
        await fixture.Renderer.InvokeAsync(page, "StartFromIntro");

        Members.Get<string>(page, "_owner").Should().Be(ActivityPageFixture.OwnerA);
        Members.Get<List<Challenge>>(page, "sentences").Should().HaveCount(2);
        Members.Get<int>(page, "currentIndex").Should().Be(0);
        fixture.Toasts.Clear();
        fixture.Ai.Prompts.Clear();
        return page;
    }

    private static Task SetInputAsync(ActivityPageFixture fixture, TranslationPage page, string text) =>
        fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", text));
}
