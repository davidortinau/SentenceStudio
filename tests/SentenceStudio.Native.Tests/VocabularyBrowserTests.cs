using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>The shared Vocabulary browser core used by the Android head (W2, AF-N08).</summary>
public sealed class VocabularyBrowserTests
{
    [Fact]
    public async Task Browser_ListsOnlyTheActiveOwnersWordsAndRefusesAnotherOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        using var browser = Create(fixture);

        Assert.False(await browser.OpenAsync(NativeSliceFixture.UserB));
        Assert.False(browser.IsOpen);
        Assert.False(await browser.OpenAsync(string.Empty));

        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));
        var list = browser.ListState;
        Assert.False(list.Blocked);
        Assert.Null(list.ErrorKey);
        Assert.Equal(4, list.FilteredWords.Count);
        Assert.All(list.FilteredWords, word =>
            Assert.StartsWith($"word-{NativeSliceFixture.UserA}-", word.Id));
    }

    [Fact]
    public async Task Browser_FiltersPersistPerOwnerAndClearRestoresAllWords()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));

        browser.OpenPhoneFilters();
        browser.ChangeFilter("status", "known", live: false);
        // The sheet edits a draft; the list changes only on Apply.
        Assert.Equal(4, browser.ListState.FilteredWords.Count);
        browser.ApplyPhoneFilters();
        Assert.Empty(browser.ListState.FilteredWords);
        Assert.Equal("status:known", fixture.Preferences.Get(
            $"vocabulary_query_{NativeSliceFixture.UserA}", string.Empty));

        browser.ClearFilters();
        Assert.Equal(4, browser.ListState.FilteredWords.Count);
        Assert.Equal(string.Empty, fixture.Preferences.Get(
            $"vocabulary_query_{NativeSliceFixture.UserA}", "unset"));
    }

    [Fact]
    public async Task Browser_WordDraftNeedsBothTermsAndCloseDiscardsIt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));
        var wordId = fixture.WordId(NativeSliceFixture.UserA, 0);

        await browser.OpenWordAsync(wordId);
        var edit = browser.EditState;
        Assert.True(browser.IsFormOpen);
        Assert.NotNull(edit.Detail);
        Assert.True(edit.CanSave);
        var original = edit.Draft!.TargetTerm;

        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.TargetTerm), " ");
        Assert.False(browser.EditState.CanSave);
        await browser.SaveAsync();
        Assert.Equal("VocabWordEdit_BothRequired", browser.EditState.ErrorKey);

        browser.CloseWord();
        Assert.False(browser.IsFormOpen);
        await browser.OpenWordAsync(wordId);
        Assert.Equal(original, browser.EditState.Draft!.TargetTerm);
    }

    [Fact]
    public async Task Browser_OwnerChangeBlocksAndDropsTheCatalog()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));

        fixture.Activate(NativeSliceFixture.UserB);

        var list = browser.ListState;
        Assert.True(list.Blocked);
        Assert.Empty(list.FilteredWords);
        Assert.Equal("Vocabulary_NoActiveProfile", browser.EditState.ErrorKey);
        await browser.OpenWordAsync(fixture.WordId(NativeSliceFixture.UserB, 0));
        Assert.False(browser.IsFormOpen);
    }

    [Theory]
    [InlineData(NativeVocabularySaveStatus.Duplicate, "VocabWordEdit_DuplicateExists")]
    [InlineData(NativeVocabularySaveStatus.Conflict, "NativeVocabulary_Conflict")]
    [InlineData(NativeVocabularySaveStatus.SharedReadOnly, "NativeVocabulary_SharedReadOnly")]
    [InlineData(NativeVocabularySaveStatus.NotFoundOrForbidden, "VocabWordEdit_NotFound")]
    public void SaveErrorKey_MatchesTheSourceMessages(NativeVocabularySaveStatus status, string key)
    {
        var (detail, draft) = Detail(LexicalUnitType.Word, links: false, resources: ["r1"]);
        Assert.Equal(key, VocabularyBrowser.SaveErrorKey(status, detail, draft));
    }

    [Fact]
    public void SaveErrorKey_ExplainsInvalidSaves()
    {
        var (linked, linkedDraft) = Detail(LexicalUnitType.Word, links: true, resources: ["r1"]);
        Assert.Equal("NativeVocabulary_ConstituentsLocked", VocabularyBrowser.SaveErrorKey(
            NativeVocabularySaveStatus.Invalid, linked,
            linkedDraft with { LexicalUnitType = LexicalUnitType.Phrase }));
        var (orphan, orphanDraft) = Detail(LexicalUnitType.Word, links: false, resources: []);
        Assert.Equal("NativeVocabulary_AssociationRequired", VocabularyBrowser.SaveErrorKey(
            NativeVocabularySaveStatus.Invalid, orphan, orphanDraft));
        var (plain, plainDraft) = Detail(LexicalUnitType.Word, links: false, resources: ["r1"]);
        Assert.Equal("VocabWordEdit_SaveFailed", VocabularyBrowser.SaveErrorKey(
            NativeVocabularySaveStatus.Invalid, plain, plainDraft));
    }

    private static VocabularyBrowser Create(NativeSliceFixture fixture) =>
        new(fixture.Provider.GetRequiredService<INativeResourceStore>(),
            fixture.UserScope,
            fixture.Preferences, new ScratchQuizMedia(),
            NullLogger<VocabularyBrowser>.Instance, key => key);

    private static (NativeVocabularyDetail, NativeVocabularyEditDraft) Detail(
        LexicalUnitType type, bool links, IReadOnlyList<string> resources)
    {
        var now = DateTime.UtcNow;
        var entry = new NativeVocabularyEntry("w", "target", "native", type, null, null,
            "Korean", now, now, "Basic", LearningStatus.Unknown, false, []);
        var draft = new NativeVocabularyEditDraft("w", now, "target", "native", null,
            "Korean", null, null, null, type, resources);
        return (new NativeVocabularyDetail(entry, draft, [], null, [], [], [],
            IsSharedWithAnotherOwner: false, HasConstituentLinks: links,
            DeleteBlock: NativeVocabularyDeleteBlock.None), draft);
    }
}
