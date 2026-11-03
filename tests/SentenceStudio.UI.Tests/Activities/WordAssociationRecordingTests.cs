using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using WordAssociationPage = SentenceStudio.WebUI.Pages.WordAssociation;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backports B4 (Word Association wrote rows with an empty profile id when none was active) and
/// B11 (an AI reply with no verdicts, or a failed AI call, was recorded as a wrong attempt).
/// </summary>
public sealed class WordAssociationRecordingTests
{
    private const string GradeFailed = "Error grading clues. Please try again.";

    [Fact]
    public async Task Grade_AiReturnsNoVerdicts_RecordsNothingAndKeepsTheCluePending()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithWordAsync(fixture);
        fixture.Ai.Respond = _ => new WordAssociationGradeResponse();

        await fixture.Renderer.InvokeAsync(page, "GradeCurrentWord");

        fixture.Ai.Prompts.Should().ContainSingle();
        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty("no verdict is not a wrong answer");
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
        PendingClueCount(page).Should().Be(1, "the learner can grade the clue again");
        fixture.Toasts.Should().Contain(t => t.Text == GradeFailed && t.Type == ToastType.Error);
        Members.Get<bool>(page, "isGrading").Should().BeFalse();
    }

    [Fact]
    public async Task Grade_AiCallFails_RecordsNothing()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithWordAsync(fixture);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");

        await fixture.Renderer.InvokeAsync(page, "GradeCurrentWord");

        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
        PendingClueCount(page).Should().Be(1);
        fixture.Toasts.Should().Contain(t => t.Text == GradeFailed);
    }

    [Fact]
    public async Task Grade_WithVerdicts_RecordsTheAttemptAndActivityForTheOwner()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithWordAsync(fixture);
        fixture.Ai.Respond = _ => new WordAssociationGradeResponse
        {
            Entries = { new ClueGrade { Text = "물", Related = true } }
        };

        await fixture.Renderer.InvokeAsync(page, "GradeCurrentWord");

        var progress = (await fixture.RowsAsync<VocabularyProgress>()).Should().ContainSingle().Subject;
        progress.UserId.Should().Be(ActivityPageFixture.OwnerA);
        var activity = (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle().Subject;
        activity.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        activity.Activity.Should().Be("WordAssociation");
        activity.Accuracy.Should().Be(100);
        PendingClueCount(page).Should().Be(0);
    }

    [Fact]
    public async Task Grade_ProfileChangedWhileGrading_DiscardsTheGrade()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithWordAsync(fixture);
        fixture.Ai.Respond = _ => new WordAssociationGradeResponse
        {
            Entries = { new ClueGrade { Text = "물", Related = true } }
        };
        fixture.Ai.Hold();

        var grading = fixture.Renderer.InvokeAsync(page, "GradeCurrentWord");
        await fixture.Ai.Started;
        fixture.Preferences.ActiveId = ActivityPageFixture.OwnerB;
        fixture.Ai.Release();
        await grading;

        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
    }

    [Fact]
    public async Task NoActiveProfile_LoadsNoRoundAndSavesNoScore()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        fixture.Preferences.ActiveId = string.Empty;

        var (_, page) = await fixture.Renderer.RenderAsync<WordAssociationPage>();
        Members.Get<List<VocabularyWord>>(page, "words").Should().BeEmpty();
        fixture.Toasts.Should().Contain(t => t.Text == "No active user profile. Please select a profile first.");

        await fixture.Renderer.InvokeAsync(page, "FinishRound");

        (await fixture.RowsAsync<WordAssociationScore>()).Should().BeEmpty("an empty profile id is never written");
        Members.Get<bool>(page, "showSessionSummary").Should().BeFalse();
        fixture.Ai.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task FinishRound_ForTheOwner_SavesTheRoundScore()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenWithWordAsync(fixture);
        fixture.Ai.Respond = _ => new WordAssociationGradeResponse
        {
            Entries = { new ClueGrade { Text = "물", Related = true, IsCloze = true } }
        };
        await fixture.Renderer.InvokeAsync(page, "GradeCurrentWord");

        await fixture.Renderer.InvokeAsync(page, "FinishRound");

        var score = (await fixture.RowsAsync<WordAssociationScore>()).Should().ContainSingle().Subject;
        score.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        score.RoundScore.Should().Be(2);
        Members.Get<bool>(page, "showSessionSummary").Should().BeTrue();
    }

    [Fact]
    public async Task Service_RefusesAnAbsentOwner()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (resource, _) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var service = fixture.Services.GetRequiredService<WordAssociationService>();

        (await service.GetRoundWordsAsync(string.Empty, resource.Id)).Should().BeEmpty();
        (await service.GetRoundWordsAsync(ActivityPageFixture.OwnerB, resource.Id)).Should().BeEmpty();
        (await service.SaveRoundScoreAsync(string.Empty, 3, 2, ["w"])).Should().BeFalse();
        (await fixture.RowsAsync<WordAssociationScore>()).Should().BeEmpty();
        (await service.GetRoundWordsAsync(ActivityPageFixture.OwnerA, resource.Id)).Should().ContainSingle();
    }

    /// <summary>Opens the page for owner A with one owned word and one pending clue.</summary>
    private static async Task<WordAssociationPage> OpenWithWordAsync(ActivityPageFixture fixture)
    {
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var (_, page) = await fixture.Renderer.RenderAsync<WordAssociationPage>();
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            Members.Set(page, "words", words);
            Members.Set(page, "currentWordIndex", 0);
            Members.Set(page, "userInput", "물");
        });
        await fixture.Renderer.InvokeAsync(page, "AddClue");
        fixture.Toasts.Clear();
        PendingClueCount(page).Should().Be(1);
        return page;
    }

    private static int PendingClueCount(WordAssociationPage page)
    {
        var clues = Members.Get<System.Collections.IDictionary>(page, "allClues");
        var count = 0;
        foreach (System.Collections.IEnumerable list in clues.Values)
            foreach (var clue in list)
                if (!(bool)clue.GetType().GetProperty("Graded")!.GetValue(clue)!)
                    count++;
        return count;
    }
}
