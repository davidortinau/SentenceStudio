using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Adding a word from the Vocabulary page (source VocabularyWordEdit add mode; Captain
/// 2026-11-10: the Vocabulary toolbar has an Add option like Resources and Skills).
/// </summary>
public sealed class VocabularyNewWordTests
{
    private const string A = NativeSliceFixture.UserA;
    private const string B = NativeSliceFixture.UserB;

    [Fact]
    public async Task Browser_AddsTheOwnersWord_InTheirTargetLanguage_AndListsIt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));

        await browser.OpenNewWordAsync();
        var edit = browser.EditState;
        Assert.True(browser.IsCreatingWord);
        Assert.True(edit.Creating);
        // Source: a new word starts in the learner's target language, as a Word, in no resource.
        Assert.Equal(("Korean", LexicalUnitType.Word), (edit.Draft!.Language, edit.Draft.LexicalUnitType));
        Assert.Empty(edit.Draft.ResourceIds);
        Assert.Contains(edit.Detail!.AvailableResources, resource => resource.Id == fixture.ResourceId(A));
        // Nothing to delete or compare yet, and no sentences until it is saved.
        Assert.False(edit.CanDelete);
        Assert.False(edit.CanFindDuplicates);
        Assert.False(edit.CanSave);

        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.TargetTerm), " 바다 ");
        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.NativeTerm), "sea");
        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.Tags), "nature");
        browser.ToggleResource(fixture.ResourceId(A), true);
        Assert.True(browser.EditState.CanSave);
        await browser.SaveAsync();

        Assert.False(browser.IsFormOpen);
        Assert.Equal("VocabWordEdit_AddedSuccess", browser.ListState.Feedback);
        var listed = Assert.Single(browser.ListState.Catalog.Words, word => word.TargetTerm == "바다");
        Assert.Equal(fixture.ResourceId(A), Assert.Single(listed.AssociatedResources).Id);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.VocabularyWords.SingleAsync(word => word.Id == listed.Id);
        Assert.Equal((A, "sea", "nature", "Korean"),
            (stored.UserProfileId, stored.NativeLanguageTerm, stored.Tags, stored.Language));
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(A));
    }

    [Fact]
    public async Task Browser_RefusesADuplicateOfTheOwnersWord_AndKeepsTheDraft()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        await browser.OpenNewWordAsync();
        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.TargetTerm), $"target-{A}-0");
        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.NativeTerm), $"native-{A}-0");

        await browser.SaveAsync();

        Assert.True(browser.IsCreatingWord);
        Assert.Equal("VocabWordEdit_DuplicateExists", browser.EditState.ErrorKey);
        Assert.Equal($"target-{A}-0", browser.EditState.Draft!.TargetTerm);
        browser.CloseWord();
        Assert.False(browser.IsFormOpen);
        Assert.False(browser.IsCreatingWord);
    }

    [Fact]
    public async Task Browser_StartsTheTargetFromASearchThatFoundNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));

        // Source GetInitialTargetTermForAdd: the search's words, without its filters.
        browser.Search("type:word 바다 소리");
        await browser.OpenNewWordAsync();
        Assert.Equal("바다 소리", browser.EditState.Draft!.TargetTerm);
        browser.CloseWord();

        // A search that found words starts the form empty.
        browser.Search($"target-{A}-0");
        Assert.NotEmpty(browser.ListState.FilteredWords);
        await browser.OpenNewWordAsync();
        Assert.Equal(string.Empty, browser.EditState.Draft!.TargetTerm);
    }

    [Fact]
    public async Task Browser_AddFormFollowsTheResourcesAReloadReads()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        await browser.OpenNewWordAsync();
        browser.ToggleResource(fixture.ResourceId(A), true);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(new LearningResource { Id = "a-new", UserProfileId = A, Title = "New list" });
            db.LearningResources.Remove(await db.LearningResources.SingleAsync(
                resource => resource.Id == fixture.ResourceId(A)));
            await db.SaveChangesAsync();
        }
        await browser.ReloadAsync();

        var edit = browser.EditState;
        Assert.True(edit.Creating);
        Assert.Contains(edit.Detail!.AvailableResources, resource => resource.Id == "a-new");
        Assert.DoesNotContain(edit.Detail.AvailableResources, resource => resource.Id == fixture.ResourceId(A));
        // A chosen resource that went away leaves the choice.
        Assert.Empty(edit.Draft!.ResourceIds);
    }

    [Fact]
    public async Task Browser_AddWaitsForTheList()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        // Not open yet: no list, no resources to choose from.
        await browser.OpenNewWordAsync();
        Assert.False(browser.IsFormOpen);
    }

    [Fact]
    public async Task Store_LinksOnlyTheOwnersOrdinaryResources()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        await fixture.SeedAsync(B);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(new LearningResource
            {
                Id = "a-smart", UserProfileId = A, Title = "Daily review", IsSmartResource = true
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(A);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        NativeVocabularyEditDraft Draft(params string[] resources) => new(string.Empty,
            DateTime.UtcNow, "하늘", "sky", null, "Korean", null, null, null,
            LexicalUnitType.Word, resources);

        Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
            (await store.CreateOwnedVocabularyAsync(A, Draft(fixture.ResourceId(B)), default)).Status);
        Assert.Equal(NativeVocabularySaveStatus.Invalid,
            (await store.CreateOwnedVocabularyAsync(A, Draft("a-smart"), default)).Status);
        Assert.Equal(NativeVocabularySaveStatus.Invalid,
            (await store.CreateOwnedVocabularyAsync(A, Draft() with { NativeTerm = " " }, default)).Status);
        Assert.Equal(NativeVocabularySaveStatus.Invalid,
            (await store.CreateOwnedVocabularyAsync(A, Draft() with { LexicalUnitType = LexicalUnitType.Idiom }, default)).Status);
        // Not the active owner.
        Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
            (await store.CreateOwnedVocabularyAsync(B, Draft(), default)).Status);
        using (var scope = fixture.Scope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .VocabularyWords.CountAsync(word => word.TargetLanguageTerm == "하늘"));

        var created = await store.CreateOwnedVocabularyAsync(A, Draft(fixture.ResourceId(A)), default);
        Assert.True(created.Saved);
        Assert.NotNull(await store.GetOwnedVocabularyAsync(A, created.WordId!, default));
        Assert.Equal("Korean", await store.GetOwnerTargetLanguageAsync(A, default));
    }

    private static VocabularyBrowser Create(NativeSliceFixture fixture) =>
        new(fixture.Provider.GetRequiredService<INativeResourceStore>(),
            fixture.UserScope,
            fixture.Preferences, new ScratchQuizMedia(),
            NullLogger<VocabularyBrowser>.Instance, key => key);
}
