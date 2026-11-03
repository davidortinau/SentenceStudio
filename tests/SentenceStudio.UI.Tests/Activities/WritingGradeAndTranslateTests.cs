using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using WritingPage = SentenceStudio.WebUI.Pages.Writing;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backports B7/B36 (the Writing translate helper saved every AI translation as an unowned,
/// resource-less VocabularyWord) and B33 (a grade in flight could land after the learner moved
/// on, recording the wrong sentence or landing in the next session).
/// </summary>
public sealed class WritingGradeAndTranslateTests
{
    private static GradeResponse Grade(double accuracy) => new()
    {
        Accuracy = accuracy,
        Fluency = 70,
        VocabularyAnalysis = new List<VocabularyAnalysis>()
    };

    [Fact]
    public async Task Translate_AiAnswer_IsShownButNoVocabularyWordIsSaved()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        var wordsBefore = (await fixture.RowsAsync<VocabularyWord>()).Count;
        fixture.Ai.Respond = _ => "the sea is blue";
        await SetInputAsync(fixture, page, "바다가 파래요");

        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        fixture.Toasts.Should().Contain(t => t.Text == "the sea is blue" && t.Type == ToastType.Info);
        (await fixture.RowsAsync<VocabularyWord>()).Should().HaveCount(wordsBefore,
            "a lookup is not a vocabulary word (it had no owner resource and blocked device sync)");
    }

    [Fact]
    public async Task Translate_AiFails_ShowsTranslationFailed()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");
        await SetInputAsync(fixture, page, "바다");

        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        fixture.Toasts.Should().Contain(t => t.Text == "Translation failed." && t.Type == ToastType.Error);
    }

    [Fact]
    public async Task Grade_RecordsTheSubmittedSentenceForTheOwner_NotTextTypedWhileGrading()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => Grade(90);
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "바다가 좋아요");

        var grading = fixture.Renderer.InvokeAsync(page, "GradeMe");
        await fixture.Ai.Started;
        await SetInputAsync(fixture, page, "typed while grading");
        await fixture.Renderer.InvokeAsync(page, "GradeMe");   // Enter again while grading
        fixture.Ai.Release();
        await grading;

        fixture.Ai.Prompts.Should().ContainSingle("a second submit while grading is ignored");
        var activity = (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle().Subject;
        activity.Input.Should().Be("바다가 좋아요");
        activity.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        Members.Get<List<Sentence>>(page, "sentences").Should().ContainSingle()
            .Which.Answer.Should().Be("바다가 좋아요");
    }

    [Fact]
    public async Task Grade_InFlight_ContinuePracticeWaits_SoTheGradeCountsInItsOwnSession()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => Grade(80);
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "첫 문장");

        var grading = fixture.Renderer.InvokeAsync(page, "GradeMe");
        await fixture.Ai.Started;
        await fixture.Renderer.InvokeAsync(page, "ShowSummary");
        await fixture.Renderer.InvokeAsync(page, "ContinueAfterSummary");

        Members.Get<bool>(page, "showSessionSummary").Should().BeTrue("Continue waits for the grade");
        fixture.Ai.Release();
        await grading;

        Members.Get<int>(page, "sessionGradedCount").Should().Be(1);
        Members.Get<List<Sentence>>(page, "sentences").Should().ContainSingle();
        (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle();
    }

    [Fact]
    public async Task Grade_InFlightWhenTheLearnerLeaves_RecordsNothing()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => Grade(95);
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "떠나기 전에");

        var grading = fixture.Renderer.InvokeAsync(page, "GradeMe");
        await fixture.Ai.Started;
        await fixture.Renderer.InvokeAsync(page, "GoBack");
        fixture.Ai.Release();
        await grading;

        fixture.Navigation.Visited.Should().ContainSingle();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
        Members.Get<List<Sentence>>(page, "sentences").Should().BeEmpty();
        fixture.Toasts.Should().NotContain(t => t.Text.StartsWith("Graded!"));
    }

    [Fact]
    public async Task NoActiveProfile_LoadsNothingAndGradesNothing()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        fixture.Preferences.ActiveId = string.Empty;
        var (_, page) = await fixture.Renderer.RenderAsync<WritingPage>();
        fixture.Toasts.Should().Contain(t => t.Text == "No active user profile. Please select a profile first.");
        await SetInputAsync(fixture, page, "누구?");

        await fixture.Renderer.InvokeAsync(page, "GradeMe");
        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        fixture.Ai.Prompts.Should().BeEmpty();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
    }

    private static async Task<WritingPage> OpenAsync(ActivityPageFixture fixture)
    {
        var (_, page) = await fixture.Renderer.RenderAsync<WritingPage>();
        Members.Get<string>(page, "_owner").Should().Be(ActivityPageFixture.OwnerA);
        fixture.Toasts.Clear();
        return page;
    }

    private static Task SetInputAsync(ActivityPageFixture fixture, WritingPage page, string text) =>
        fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", text));
}
