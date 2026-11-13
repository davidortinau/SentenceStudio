namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Captain 2026-11-12 (Duotopia review): search at the bottom like Notes, the count under the
/// title, and another resource can be opened while one is open beside the list.
/// </summary>
public sealed class NativeLibraryNotesStyleContractTests
{
    private static string Source(string relative) => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "SentenceStudio.Native.iOS", relative)).Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void NarrowSearchSitsInTheBottomToolbarLikeNotes()
    {
        var browse = Source("Features/Browse/BrowserPage.cs");
        Assert.Contains(".WithSearchPlacement(SwiftSearchPlacement.BottomBar)", browse);
        Assert.Contains(".WithSearchKeepingToolbar()", browse);
    }

    [Fact]
    public void ResourcesCountIsTheTitlesSubtitleNotAListRow()
    {
        var view = Source("Features/LearningResources/LearningResourcesView.cs");
        Assert.DoesNotContain("native.resources.count", view);
        Assert.Contains("internal static string CountText(", view);
        Assert.Contains(".WithNavigationSubtitle(_app.ResourcesSubtitle)",
            Source("App/ApplicationScene.Library.cs"));
        Assert.Contains("_resourcesSlot.WithNavigationSubtitle(_app.ResourcesSubtitle);",
            Source("App/ApplicationScene.cs"));
    }

    [Fact]
    public void AnotherResourceOpensWhileOneIsOpenUnlessItHasUnsavedEdits()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var open = coordinator[coordinator.IndexOf("private async Task OpenAsync(string id)",
                StringComparison.Ordinal)..coordinator.IndexOf("private void ResetForm(bool creating)",
                StringComparison.Ordinal)];
        Assert.DoesNotContain("_disposed || _formOpen || IsNavigationBlocked", open);
        Assert.Contains("if (FormHasUnsavedChanges)", open);
        Assert.Contains("_formErrorKey = \"ResourceEdit_SaveBeforeOpeningAnother\";", open);
        Assert.Contains("if (!open && !_resources.IsSwitchingForm)",
            Source("App/ApplicationController.cs"));
        Assert.Contains("CloseForm(publish: false);", open);
    }
}
