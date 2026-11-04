using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using ReadingPage = SentenceStudio.WebUI.Pages.Reading;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backport B48 (Captain 2026-11-04, decision 6): "Remember this word" saved the word, then
/// linked it to the resource in a second step, so a failed link left an owned word with no
/// resource. It now uses the native create-and-link path in one step.
/// </summary>
public sealed class ReadingRememberWordTests
{
    [Fact]
    public async Task RememberWord_CreatesTheOwnersWordLinkedToTheResource()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (resource, _) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("하늘", "sky"));
        var page = await OpenAsync(fixture, resource, "바다", "sea");

        await fixture.Renderer.InvokeAsync(page, "SaveWordToVocabulary");

        var word = (await fixture.RowsAsync<VocabularyWord>()).Should()
            .ContainSingle(w => w.TargetLanguageTerm == "바다").Subject;
        word.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        word.NativeLanguageTerm.Should().Be("sea");
        (await fixture.RowsAsync<ResourceVocabularyMapping>()).Should()
            .Contain(m => m.ResourceId == resource.Id && m.VocabularyWordId == word.Id);
        Members.Get<VocabularyWord?>(page, "selectedVocabWord")!.Id.Should().Be(word.Id);
        Members.Get<List<VocabularyWord>>(page, "vocabularyWords").Should().Contain(w => w.Id == word.Id);
        fixture.Toasts.Should().Contain(t => t.Type == ToastType.Success);
    }

    [Fact]
    public async Task RememberWord_TheLearnerAlreadyHasTheWord_LinksItInsteadOfDuplicating()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (_, existing) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var (reading, _) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("하늘", "sky"));
        var page = await OpenAsync(fixture, reading, "바다", "sea");

        await fixture.Renderer.InvokeAsync(page, "SaveWordToVocabulary");

        (await fixture.RowsAsync<VocabularyWord>()).Where(w => w.TargetLanguageTerm == "바다")
            .Should().ContainSingle().Which.Id.Should().Be(existing[0].Id);
        (await fixture.RowsAsync<ResourceVocabularyMapping>()).Should()
            .Contain(m => m.ResourceId == reading.Id && m.VocabularyWordId == existing[0].Id);
    }

    /// <summary>
    /// The resource already has the word (for example it arrived from another device after the
    /// page loaded): that is the outcome the learner asked for, not a failure.
    /// </summary>
    [Fact]
    public async Task RememberWord_AlreadyInTheResource_ShowsItAsInVocabulary_NotAnError()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (resource, existing) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var page = await OpenAsync(fixture, resource, "바다", "sea");
        var activitiesBefore = (await fixture.RowsAsync<UserActivity>()).Count;

        await fixture.Renderer.InvokeAsync(page, "SaveWordToVocabulary");

        fixture.Toasts.Should().NotContain(t => t.Type == ToastType.Error);
        fixture.Toasts.Should().Contain(t => t.Type == ToastType.Info
            && t.Text == "Already in Fixture resource.");
        Members.Get<VocabularyWord?>(page, "selectedVocabWord")!.Id.Should().Be(existing[0].Id);
        Members.Get<List<VocabularyWord>>(page, "vocabularyWords").Should().Contain(w => w.Id == existing[0].Id);
        (await fixture.RowsAsync<VocabularyWord>()).Where(w => w.TargetLanguageTerm == "바다")
            .Should().ContainSingle("nothing is duplicated");
        (await fixture.RowsAsync<UserActivity>()).Should().HaveCount(activitiesBefore,
            "no vocabulary was added, so no \"Added vocabulary\" row is written");
        Members.Get<bool>(page, "isSavingWord").Should().BeFalse();
    }

    [Fact]
    public async Task RememberWord_LinkRefused_LeavesNoWordBehind()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (foreign, _) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerB, ("하늘", "sky"));
        var page = await OpenAsync(fixture, foreign, "바다", "sea");
        var wordsBefore = (await fixture.RowsAsync<VocabularyWord>()).Count;

        await fixture.Renderer.InvokeAsync(page, "SaveWordToVocabulary");

        (await fixture.RowsAsync<VocabularyWord>()).Should().HaveCount(wordsBefore,
            "a word that cannot be linked is not saved at all");
        fixture.Toasts.Should().Contain(t => t.Type == ToastType.Error);
        Members.Get<VocabularyWord?>(page, "selectedVocabWord").Should().BeNull();
    }

    /// <summary>Opens Reading for owner A on <paramref name="resource"/> with a looked-up word.</summary>
    private static async Task<ReadingPage> OpenAsync(
        ActivityPageFixture fixture, LearningResource resource, string word, string definition)
    {
        var (_, page) = await fixture.Renderer.RenderAsync<ReadingPage>();
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            Members.Set(page, "_owner", ActivityPageFixture.OwnerA);
            Members.Set(page, "resource", resource);
            Members.Set(page, "selectedWord", word);
            Members.Set(page, "wordDefinition", definition);
        });
        return page;
    }
}
