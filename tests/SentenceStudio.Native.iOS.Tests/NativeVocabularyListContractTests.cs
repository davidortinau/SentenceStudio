namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Captain 2026-11-12: the Vocabulary list is a native List with icons and swipe actions
/// (Delete asks first, Study opens Choose My Own with the word's resources); the wide grid stays.
/// </summary>
public sealed class NativeVocabularyListContractTests
{
    private static string Source(string relative) => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", relative)).Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static string ListView => Source("SentenceStudio.Native.iOS/Features/Vocabulary/VocabularyListView.cs");

    [Fact]
    public void OneColumnAndWideListModeAreANativeListAndTheGridStays()
    {
        var view = ListView;
        Assert.Contains("&& (!wide || state.ListMode);", view);
        Assert.Contains("AsNativeList(state, wide: false) ? style : null);", view);
        Assert.Contains("asList: AsNativeList(state, wide: true));", view);
        // The duplicate manager's buttons would share one list row's tap.
        Assert.Contains("state.Duplicates is null", view);
        Assert.Contains("Views.LazyVGrid(", view);
    }

    [Fact]
    public void RowsSwipeToDeleteAfterAskingAndToStudy()
    {
        var view = ListView;
        var row = view[view.IndexOf("private static SwiftView ListRow(", StringComparison.Ordinal)..];
        Assert.Contains("Views.SwipeRow(row, trailing, leading)", row);
        Assert.Contains("Views.Label(text(\"Common_Delete\"), \"trash\")", row);
        // Red, not destructive: a destructive swipe removes its row before the dialog.
        Assert.DoesNotContain("SwiftButtonRole.Destructive", row[..row.IndexOf("private static string Details(", StringComparison.Ordinal)]);
        Assert.Contains("word.AssociatedResources.Count == 0 ? []", row);
        Assert.Contains("Views.Image(word.LexicalUnitType switch", row);
        Assert.Contains("Views.ConfirmationDialog(page, message ?? string.Empty,", view);
        Assert.Contains("native.vocabulary.delete.dialog", view);
    }

    [Fact]
    public void CancelWaitsForDeleteAndStudyUsesTheWordsResources()
    {
        var coordinator = Source("SentenceStudio.Native.iOS/Features/Vocabulary/VocabularyCoordinator.cs");
        Assert.Contains("CancelDelete: CancelListDeleteLater,", coordinator);
        Assert.Contains("DispatchQueue.MainQueue.DispatchAsync(() =>\n        {\n            if (!IsDisposed && PendingListDeleteId == id)", coordinator);
        Assert.Contains("PageWidth, ListDeleteMessage);", coordinator);
        Assert.Contains("_vocabulary.StudyRequested += StudyResources;",
            Source("SentenceStudio.Native.iOS/App/ApplicationController.Vocabulary.cs"));
        Assert.Contains("internal void StudyResource(string resourceId) => StudyResources([resourceId]);",
            Source("SentenceStudio.Native.iOS/App/ApplicationController.cs"));
        // The shared delete is the owner-scoped store delete the editor uses.
        Assert.Contains("_store.DeleteOwnedVocabularyAsync(\n                owner, word.Id, _lifetimeToken)",
            Source("SentenceStudio.Native/Features/Vocabulary/VocabularyBrowser.ListDelete.cs"));
    }
}
