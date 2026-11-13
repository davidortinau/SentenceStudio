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
    public async Task Browser_FiltersApplyAndClearButAnotherVisitStartsUnfiltered()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var key = $"vocabulary_query_{NativeSliceFixture.UserA}";
        // A query an earlier build saved is not restored (Captain 2026-11-12).
        fixture.Preferences.Set(key, "status:known");
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));
        Assert.False(browser.ListState.Filters.HasActiveFilters);
        Assert.Equal(4, browser.ListState.FilteredWords.Count);
        Assert.Equal(4, browser.KnownWordCount);

        browser.OpenPhoneFilters();
        browser.ChangeFilter("status", "known", live: false);
        // The sheet edits a draft; the list changes only on Apply.
        Assert.Equal(4, browser.ListState.FilteredWords.Count);
        browser.ApplyPhoneFilters();
        Assert.Empty(browser.ListState.FilteredWords);

        browser.ClearFilters();
        Assert.Equal(4, browser.ListState.FilteredWords.Count);

        browser.ChangeFilter("status", "known", live: true);
        browser.Close();
        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));
        Assert.False(browser.ListState.Filters.HasActiveFilters);
        Assert.Equal(4, browser.ListState.FilteredWords.Count);
    }

    [Fact]
    public async Task Browser_ShowsRowsAPageAtATimeAndStartsAgainForANewSearch()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));
        Assert.Equal(VocabularyBrowser.RowPage, browser.ListState.RowLimit);
        Assert.Equal(0, browser.ListState.HiddenWordCount);

        browser.ShowMoreRows();
        Assert.Equal(2 * VocabularyBrowser.RowPage, browser.ListState.RowLimit);
        browser.ChangeFilter("status", "known", live: true);
        Assert.Equal(VocabularyBrowser.RowPage, browser.ListState.RowLimit);

        browser.ShowMoreRows();
        browser.SetSort("updated");
        Assert.Equal(VocabularyBrowser.RowPage, browser.ListState.RowLimit);
        browser.ShowMoreRows();
        browser.SetListMode(!browser.ListMode);
        Assert.Equal(VocabularyBrowser.RowPage, browser.ListState.RowLimit);

        browser.ClearFilters();
        var state = browser.ListState with { RowLimit = 2 };
        Assert.Equal(4, state.FilteredWords.Count);
        Assert.Equal(2, state.ShownWords.Count());
        Assert.Equal(state.FilteredWords.Count - 2, state.HiddenWordCount);
    }

    [Fact]
    public async Task Browser_SheetSortAppliesWithTheFiltersAndCancelKeepsTheList()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));
        var sortKey = $"vocabulary_sort_{NativeSliceFixture.UserA}";
        Assert.Equal(("added", true),
            (browser.ListState.SortField, browser.ListState.SortDescending));

        // Closed sheet: its sort does nothing.
        browser.SetSheetSort("updated");
        Assert.Equal("added", browser.ListState.SheetSortField);

        browser.OpenPhoneFilters();
        browser.SetSheetSort("updated");
        Assert.Equal(("updated", true),
            (browser.ListState.SheetSortField, browser.ListState.SheetSortDescending));
        // The list keeps its sort until Apply; choosing the same sort again flips it.
        Assert.Equal("added", browser.ListState.SortField);
        browser.SetSheetSort("updated");
        Assert.False(browser.ListState.SheetSortDescending);
        browser.CancelPhoneFilters();
        Assert.Equal(("added", true),
            (browser.ListState.SortField, browser.ListState.SortDescending));
        Assert.Equal("unset", fixture.Preferences.Get(sortKey, "unset"));

        // A reopened sheet starts from the list's sort; Apply sorts the list and saves it.
        browser.OpenPhoneFilters();
        Assert.Equal(("added", true),
            (browser.ListState.SheetSortField, browser.ListState.SheetSortDescending));
        browser.SetSheetSort("updated");
        browser.ApplyPhoneFilters();
        var list = browser.ListState;
        Assert.Equal(("updated", true), (list.SortField, list.SortDescending));
        Assert.Equal("updated:desc", fixture.Preferences.Get(sortKey, string.Empty));
        Assert.Equal(
            list.FilteredWords.OrderByDescending(word => word.UpdatedAt)
                .ThenBy(word => word.Id, StringComparer.Ordinal).Select(word => word.Id),
            list.FilteredWords.Select(word => word.Id));

        // The wide sort buttons apply at once, with the same flip.
        browser.SetSort("updated");
        Assert.Equal(("updated", false),
            (browser.ListState.SortField, browser.ListState.SortDescending));
        Assert.Equal("updated:asc", fixture.Preferences.Get(sortKey, string.Empty));

        // A wide sort while the sheet is open moves the sheet's draft too, so Apply keeps it.
        browser.OpenPhoneFilters();
        browser.SetSort("added");
        Assert.Equal(("added", true),
            (browser.ListState.SheetSortField, browser.ListState.SheetSortDescending));
        browser.ApplyPhoneFilters();
        Assert.Equal(("added", true),
            (browser.ListState.SortField, browser.ListState.SortDescending));
        browser.SetSort("updated");
        browser.SetSort("updated");

        // The arrangement a head starts its results again for: sort and grid/list.
        Assert.Equal("updated:asc:grid", browser.Arrangement);
        browser.SetListMode(true);
        Assert.Equal("updated:asc:list", browser.Arrangement);
        browser.Search("병");
        Assert.Equal("updated:asc:list", browser.Arrangement);
    }

    [Fact]
    public async Task Browser_FilterPanelHiddenIsRememberedPerOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        using (var browser = Create(fixture))
        {
            Assert.True(await browser.OpenAsync(NativeSliceFixture.UserA));
            Assert.False(browser.ListState.FilterPanelHidden);
            browser.ToggleFilterPanel();
            Assert.True(browser.ListState.FilterPanelHidden);
            Assert.True(browser.FilterPanelHidden);
        }
        Assert.Equal("hidden", fixture.Preferences.Get(
            $"vocabulary_filter_panel_{NativeSliceFixture.UserA}", string.Empty));

        using (var reopened = Create(fixture))
        {
            Assert.True(await reopened.OpenAsync(NativeSliceFixture.UserA));
            Assert.True(reopened.ListState.FilterPanelHidden);
            reopened.ToggleFilterPanel();
            Assert.False(reopened.ListState.FilterPanelHidden);
        }

        // Another owner keeps their own (shown by default).
        fixture.Activate(NativeSliceFixture.UserB);
        using var other = Create(fixture);
        Assert.True(await other.OpenAsync(NativeSliceFixture.UserB));
        Assert.False(other.ListState.FilterPanelHidden);
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
        // A word with no links and no progress is still the owner's (Captain 2026-11-06): no
        // "keep a resource" reason exists for it any more.
        var (orphan, orphanDraft) = Detail(LexicalUnitType.Word, links: false, resources: []);
        Assert.Equal("VocabWordEdit_SaveFailed", VocabularyBrowser.SaveErrorKey(
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
            HasConstituentLinks: links,
            DeleteBlock: NativeVocabularyDeleteBlock.None), draft);
    }
}
