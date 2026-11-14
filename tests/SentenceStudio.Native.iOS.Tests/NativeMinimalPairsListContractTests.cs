using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Minimal Pairs on iOS (Captain 2026-11-13, "clean up the UI and workflow"): the list is a
/// native List under the system search field with Create Pair as the toolbar's Add, and the
/// create page lists a word's matches only while its search has text.
/// </summary>
public sealed class NativeMinimalPairsListContractTests
{
    [Fact]
    public void List_IsANativeListWithSearchAddAndSwipeStudyAndDelete()
    {
        var screen = Read("Features/Activities/MinimalPairsScreen.cs");

        Assert.Contains("BrowserPage.SingleColumn(ListPrefix, rows, style.ViewportWidth,", screen);
        Assert.Contains("new BrowserPage.PageSearch(text(NativeMinimalPairsText.Search), _search, OnSearch,",
            screen);
        Assert.Contains("BrowserPage.AddButton(ListPrefix, text(NativeMinimalPairsText.CreatePair),", screen);
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Segmented)", screen);
        Assert.Contains("Views.SwipeRow(row, trailing, leading)", screen);
        Assert.Contains("_presenter.StudyPairAsync(pair.Id, token)", screen);
        Assert.Contains("_presenter.RequestDeleteAsync(pair.Id, token)", screen);
        // The old page: a card grid, an inline Create button and a hand-drawn button group.
        Assert.DoesNotContain("Views.LazyVGrid(", screen);
        Assert.DoesNotContain("Views.ButtonGroup(", screen);
        Assert.DoesNotContain("style.LabelButton(IconText(\"plus\"", screen);
        // A disabled Start in Focus says how to choose a pair.
        Assert.Contains("text(NativeMinimalPairsText.SelectPairFirst)", screen);
    }

    [Fact]
    public void BothHosts_UpdateTheListInPlaceByVersion_AndShowTheCountUnderTheTitle()
    {
        var screen = Read("Features/Activities/MinimalPairsScreen.cs");
        var scene = Read("App/ApplicationScene.cs");
        var library = Read("App/ApplicationScene.Library.cs");
        var activity = Read("App/ApplicationScene.Activities.cs");

        Assert.Contains("public BrowserPageUpdate BrowsePage => BrowserPageUpdate.MinimalPairs;", screen);
        // One scene helper: in place, else the fresh render mounted whole with the field's focus.
        Assert.Contains("private static void UpdateBrowsePage(", scene);
        Assert.Contains("search.FocusRequested = true;", scene);
        Assert.Contains("UpdateBrowsePage(_pairsSlot, _app.RenderLibraryPairs(_style), BrowserPageUpdate.MinimalPairs,",
            library);
        Assert.Contains("_renderedActivityPageVersion != _app.ActivityPageVersion", activity);
        Assert.Contains("UpdateBrowsePage(_activitySlot, _app.RenderActivity(_style), browse, reshaped,", activity);
        Assert.Contains(".WithNavigationSubtitle(_app.LibraryPairsSubtitle)", library);
        Assert.Contains("_pairsPage.WithNavigationSubtitle(_renderedPairsSubtitle);", library);
        Assert.Contains(".WithNavigationSubtitle(_app.ActivitySubtitle)", activity);
        Assert.Contains("_activityPage.WithNavigationSubtitle(_renderedActivitySubtitle);", activity);
        Assert.Contains("NativeMinimalPairsText.PairCount", screen);
    }

    [Fact]
    public void Create_ListsMatchesOnlyWhileSearching_AndAChoiceClearsItsSearchInThePresenter()
    {
        var screen = Read("Features/Activities/MinimalPairsScreen.cs");

        Assert.Contains("if (string.IsNullOrWhiteSpace(search))", screen);
        Assert.Contains("_presenter.SelectWordAsync(slot, wordId, token, clearSearch: true)", screen);
        Assert.DoesNotContain("_presenter.SetSearch(slot, string.Empty)", screen);
    }

    private static string Read(string file) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", file)));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
