using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using ScenePage = SentenceStudio.WebUI.Pages.Scene;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backports B6 (Describe a Scene scored words against whatever profile was active when the
/// grade came back, from a vocabulary list loaded for an implicit owner) and B10 (a failed AI
/// grade was saved as a 0% card and a 0% activity row).
/// </summary>
public sealed class SceneGradeRecordingTests
{
    private const string GradeFailed = "Could not grade your description. Please try again.";

    private static GradeResponse Grade(double accuracy, params string[] usedWords) => new()
    {
        Accuracy = accuracy,
        Fluency = 70,
        VocabularyAnalysis = usedWords.Select(word => new VocabularyAnalysis
        {
            DictionaryForm = word,
            UsedForm = word,
            UsageCorrect = true
        }).ToList()
    };

    [Fact]
    public async Task Grade_AiFails_ShowsGradeFailedAndRecordsNothing()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");
        await SetInputAsync(fixture, page, "바다가 보여요");

        await fixture.Renderer.InvokeAsync(page, "SubmitDescription");

        fixture.Toasts.Should().Contain(t => t.Text == GradeFailed && t.Type == ToastType.Error);
        Members.Get<List<Sentence>>(page, "gradedSentences").Should().BeEmpty("a failed grade is not a 0% card");
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty("a failed grade is not a 0% activity row");
        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Grade_AiReturnsNoGrade_ShowsGradeFailedAndRecordsNothing()
    {
        await using var fixture = await CreateFixtureAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => null;
        await SetInputAsync(fixture, page, "하늘이 파래요");

        await fixture.Renderer.InvokeAsync(page, "SubmitDescription");

        fixture.Toasts.Should().Contain(t => t.Text == GradeFailed && t.Type == ToastType.Error);
        Members.Get<List<Sentence>>(page, "gradedSentences").Should().BeEmpty();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Grade_RecordsTheOwnersActivityAndScoresOnlyTheOwnersWords()
    {
        await using var fixture = await CreateFixtureAsync();
        var (_, ownerWords) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerB, ("하늘", "sky"));
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => Grade(85, "바다", "하늘");
        await SetInputAsync(fixture, page, "바다와 하늘이 보여요");

        await fixture.Renderer.InvokeAsync(page, "SubmitDescription");

        var activity = (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle().Subject;
        activity.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        activity.Input.Should().Be("바다와 하늘이 보여요");
        activity.Accuracy.Should().Be(85);
        var progress = (await fixture.RowsAsync<VocabularyProgress>()).Should().ContainSingle(
            "another learner's word is never scored, even when the AI names it").Subject;
        progress.UserId.Should().Be(ActivityPageFixture.OwnerA);
        progress.VocabularyWordId.Should().Be(ownerWords[0].Id);
        fixture.Toasts.Should().Contain(t => t.Text == "Excellent - Accuracy: 85%" && t.Type == ToastType.Success);
    }

    [Fact]
    public async Task Grade_ProfileChangesWhileGrading_RecordsNothingForEitherLearner()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => Grade(90, "바다");
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "바다가 넓어요");

        var grading = fixture.Renderer.InvokeAsync(page, "SubmitDescription");
        await fixture.Ai.Started;
        fixture.Preferences.ActiveId = ActivityPageFixture.OwnerB;
        fixture.Ai.Release();
        await grading;

        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty(
            "the first learner's words are never scored for the learner who is active when the grade lands");
        Members.Get<List<Sentence>>(page, "gradedSentences").Should().BeEmpty();
    }

    [Fact]
    public async Task Grade_SceneChangesWhileGrading_TheLateGradeLandsNowhere()
    {
        await using var fixture = await CreateFixtureAsync();
        var page = await OpenAsync(fixture);
        var other = new SceneImage { Url = "https://example.invalid/other.jpg", Description = "a park", UserProfileId = ActivityPageFixture.OwnerA };
        fixture.Ai.Respond = _ => Grade(75);
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "해변이에요");

        var grading = fixture.Renderer.InvokeAsync(page, "SubmitDescription");
        await fixture.Ai.Started;
        await fixture.Renderer.InvokeAsync(page, "OnGalleryItemClick", other);
        fixture.Ai.Release();
        await grading;

        Members.Get<string>(page, "imageUrl").Should().Be(other.Url);
        Members.Get<List<Sentence>>(page, "gradedSentences").Should().BeEmpty(
            "a description of the beach is not a result for the park");
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Grade_InFlightWhenTheLearnerLeaves_RecordsNothing()
    {
        await using var fixture = await CreateFixtureAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => Grade(95);
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "떠나기 전에");

        var grading = fixture.Renderer.InvokeAsync(page, "SubmitDescription");
        await fixture.Ai.Started;
        await fixture.Renderer.InvokeAsync(page, "GoBack");
        fixture.Ai.Release();
        await grading;

        fixture.Navigation.Visited.Should().ContainSingle();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
        fixture.Toasts.Should().NotContain(t => t.Type == ToastType.Success);
    }

    [Fact]
    public async Task NoActiveProfile_LoadsNothingAndGradesNothing()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Preferences.ActiveId = string.Empty;
        var (_, page) = await fixture.Renderer.RenderAsync<ScenePage>();
        fixture.Toasts.Should().Contain(t => t.Text == "No active user profile. Please select a profile first.");
        await SetInputAsync(fixture, page, "누구?");

        await fixture.Renderer.InvokeAsync(page, "SubmitDescription");

        fixture.Ai.Prompts.Should().BeEmpty();
        Members.Get<List<VocabularyWord>>(page, "userVocabulary").Should().BeEmpty();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
    }

    private static async Task<ActivityPageFixture> CreateFixtureAsync()
    {
        var fixture = await ActivityPageFixture.CreateAsync(services =>
        {
            services.AddSingleton<DeviceUserScopeProvider>();
            services.AddSingleton<IUserScopeProvider>(sp => sp.GetRequiredService<DeviceUserScopeProvider>());
            services.AddSingleton<SceneImageService>();
        });
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SceneImages.Add(new SceneImage
        {
            Url = "https://example.invalid/beach.jpg",
            Description = "a beach",
            UserProfileId = ActivityPageFixture.OwnerA
        });
        await db.SaveChangesAsync();
        return fixture;
    }

    private static async Task<ScenePage> OpenAsync(ActivityPageFixture fixture)
    {
        var (_, page) = await fixture.Renderer.RenderAsync<ScenePage>();
        Members.Get<string>(page, "_owner").Should().Be(ActivityPageFixture.OwnerA);
        Members.Get<string>(page, "imageUrl").Should().Be("https://example.invalid/beach.jpg");
        fixture.Toasts.Clear();
        return page;
    }

    private static Task SetInputAsync(ActivityPageFixture fixture, ScenePage page, string text) =>
        fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", text));
}
