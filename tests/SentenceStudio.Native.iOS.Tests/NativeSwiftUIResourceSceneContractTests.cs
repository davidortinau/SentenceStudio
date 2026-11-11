using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSwiftUIResourceSceneContractTests
{
    // Captain 2026-11-10: the Resources page uses the Vocabulary page's browse layout, and the
    // scene updates it with the same in-place updater, so typing keeps the search field mounted.
    [Fact]
    public void ResourcesRefresh_UpdatesTheMountedBrowsePageInPlace()
    {
        var scene = Source();
        var update = scene[scene.IndexOf("private void UpdateResources(",
                StringComparison.Ordinal)
            ..scene.IndexOf("private void UpdateResourceFilters(",
                StringComparison.Ordinal)];

        Assert.Contains("var current = _resourcesSlot.Children.Single();", update);
        Assert.Contains("var oldSearch = Find(current, BrowserPageUpdate.Resources.SearchId);",
            update);
        Assert.Contains("!BrowserPageUpdate.Resources.TryUpdateInPlace(current, next)", update);
        // Another layout, sort or grid/list choice: a new page, with focus handed over.
        Assert.Contains("replaceAll |= !string.Equals(arrangement, _renderedResourcesArrangement",
            update);
        Assert.Contains("Replace(_resourcesSlot, next);", update);
        Assert.Contains("_resourcesSearch.FocusRequested = true;", update);
        Assert.Contains("oldSearch.Text = _app.ResourcesSearchText;", update);
        Assert.Contains("_resourcesSearch = oldSearch;", update);
        Assert.Contains("next.Dispose();", update);
        Assert.DoesNotContain("current.RemoveAt(", update);
        Assert.DoesNotContain("current.Add(", update);
        Assert.DoesNotContain("SameBrowserControls", scene);
        Assert.DoesNotContain("UpdateResourceViewModeButton", scene);

        var updater = Browse("BrowserPageUpdate.cs");
        Assert.Contains("Resources = new(\"native.resources\", headerKeep: 0);", updater);
        // The search field and the page (while a starter resource is made) change availability
        // in place.
        Assert.Contains("node.IsEnabled = nextNode.IsEnabled;", updater);
        Assert.Contains("CopyButtonState(current.Children[index], next.Children[index]);", updater);
    }

    [Fact]
    public void ResourcesToolbar_GridListFiltersAndAddReadTheStateWhenTapped()
    {
        var browser = Feature("LearningResourcesView.cs");
        var coordinator = Feature("LearningResourcesCoordinator.cs");
        var options = Feature("LearningResourcesCoordinator.ListOptions.cs");

        Assert.Contains("BrowserPage.ViewToggle(Prefix, state.ViewMode == NativeResourceViewMode.List,",
            browser);
        Assert.Contains("BrowserPage.FiltersButton(Prefix, text(\"Vocabulary_Filters\"),", browser);
        Assert.Contains("BrowserPage.AddButton(Prefix, text(\"Resources_AddResource\"), actions.Add,",
            browser);
        // Plain toolbar icons, as on Vocabulary (no tint, no filled Add button when wide).
        Assert.DoesNotContain("WithTintColor", browser);
        Assert.DoesNotContain("PrimaryButton(text(\"Resources_AddResource\")", browser);
        Assert.DoesNotContain("Views.List(", browser);
        Assert.Contains("BrowserPage.FilterMenu(Prefix, \"type\"", browser);
        Assert.Contains("BrowserPage.SortField(Prefix, state.SortField, state.SortDescending,",
            browser);

        Assert.Contains("ToggleViewMode: ToggleViewMode,", coordinator);
        Assert.Contains("FilterType: type => FilterNow(type, _presenter.List.Language),",
            coordinator);
        Assert.Contains("FilterLanguage: language => FilterNow(_presenter.List.MediaType, language),",
            coordinator);
        Assert.Contains("_presenter.List.ViewMode == NativeResourceViewMode.Grid", options);
        Assert.Contains("ListChanged(_presenter.Filter(mediaType, language));", options);
    }

    [Fact]
    public void ResourcesPhoneSheet_HostsSortAndFiltersOverThePage()
    {
        var scene = Source();
        var library = File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/App/ApplicationScene.Library.cs")));
        var options = Feature("LearningResourcesCoordinator.ListOptions.cs");

        Assert.Contains("_resourcesSheet = Views.Sheet(_resourcesSlot, _resourcesFilterSlot,", library);
        Assert.Contains(".WithSheetDetentFraction(0.85)", library);
        Assert.Contains("_libraryStack.Push(ApplicationFormRoutes.LibraryResources, _resourcesSheet);",
            library);
        Assert.Contains("UpdateResourceFilters(appearanceChanged);", library);
        Assert.Contains("Replace(_resourcesFilterSlot, _app.RenderResourceFilters(_style));", scene);
        Assert.Contains("_resourcesSheet.IsPresented = _app.IsResourceFilterPresented;", scene);
        // Becoming wide drops the sheet's choices, as on Vocabulary.
        Assert.Contains("_app.CancelResourceFilters();", scene);
        Assert.Contains("if (wide && IsFilterSheetPresented)", options);
        Assert.Contains("Apply: () => ListChanged(_presenter.ApplyFilterSheet()),", options);
    }

    [Fact]
    public void WidthBreakpoint_RebuildsHeaderAndFormWhilePreservingSearchDraftAndFocus()
    {
        var scene = Source();
        Assert.Contains("if (crossedDesktopBreakpoint)", scene);
        Assert.Contains("UpdateResources(replaceAll: true);", scene);
        Assert.Contains("var wasSearching = _resourcesSearch?.FocusRequested == true;",
            scene);
        Assert.Contains("_resourcesSearch.FocusRequested = true;", scene);
        Assert.Contains("FindFocusedInput(_formSlot)?.AccessibilityIdentifier",
            scene);
        Assert.Contains("Replace(_formSlot, ResourceFormContent());",
            scene);
        Assert.Contains("input.FocusRequested = true;", scene);
        Assert.Contains("Views.QuickAddSuggestions(", scene);
        Assert.Contains("ResourceFormContent()", scene);
        Assert.Contains("Views.ResourceDeleteConfirmation(_fileImporter,", scene);
        Assert.Contains("_deleteConfirmation.DeletePending =", scene);
        Assert.Contains("Views.StarterBlockingOverlay(", scene);
    }

    [Fact]
    public void QuickAddSearch_UpdatesDirectSuggestionChildrenWithoutRemountingForm()
    {
        var scene = Source();
        var controller = File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/App/ApplicationController.cs")));
        var refresh = scene[scene.IndexOf(
                "private void RefreshQuickSuggestions()", StringComparison.Ordinal)
            ..scene.IndexOf("private void UpdateResourceTitle(",
                StringComparison.Ordinal)];

        Assert.Contains("ResourceQuickAddSuggestionsVersion =>", controller);
        Assert.Contains("_resources?.QuickAddSuggestionsVersion ?? -1", controller);
        Assert.Contains("_quickSuggestionSlot = _app.RenderResourceQuickAddSuggestions(_style)",
            scene);
        Assert.DoesNotContain("_quickSuggestionSlot = Views.VStack(", scene);
        Assert.Contains("RefreshQuickSuggestions();", scene);
        Assert.Contains("DispatchQueue.MainQueue.DispatchAsync(() =>", refresh);
        Assert.Contains("!ReferenceEquals(_quickSuggestionSlot, suggestions)",
            refresh);
        Assert.Contains("ReplaceContainerChildren(suggestions,", refresh);
        Assert.Contains("var child = next.Children[0];", refresh);
        Assert.Contains("slot.Add(child);", refresh);
        Assert.Contains("slot.Spacing = next.Spacing;", refresh);
        Assert.DoesNotContain("Replace(_formSlot, ResourceFormContent());",
            refresh);
    }

    [Fact]
    public void ExtractedViews_RegisterTheMountedSearchFieldsAndDirectVocabularyChildren()
    {
        var scene = Source();
        var browser = Feature("LearningResourcesView.cs");
        var form = Feature("LearningResourceView.cs");
        var vocabulary = Feature("LearningResourceVocabularyView.cs");
        var quickAdd = Feature("LearningResourceQuickAddView.cs");
        var coordinator = Feature("LearningResourcesCoordinator.cs");

        Assert.Contains("var children = new List<SwiftView> { SearchRow(state, style, text, actions) };",
            browser);
        Assert.Contains("return BrowserPage.WithToolbar(Prefix, page, style, actions.PageWidthChanged,",
            browser);
        Assert.Contains("_resourcesSearch = Find(_resourcesSlot, BrowserPageUpdate.Resources.SearchId);",
            scene);
        Assert.Contains("_resourcesSearch = oldSearch;", scene);

        Assert.Contains("inputs.RegisterQuickSearch(input);", quickAdd);
        Assert.Contains("_quickSearchField = view;", coordinator);
        Assert.Contains("inputs.RegisterVocabularySection(vocabularyCard);", form);
        Assert.Contains("fields.Add(vocabularyCard);", form);
        Assert.Contains("fields.Add(style.Text(Views.Text(creating", vocabulary);
        Assert.Contains("fields.Add(rows);", vocabulary);
        Assert.Contains("section.Children[0].AccessibilityIdentifier", coordinator);
        Assert.Contains("section.Add(rows);", coordinator);
        Assert.Contains("LearningResourceVocabularyView.AppendEditVocabularyRow(rows,",
            coordinator);
        Assert.Contains("slot.RemoveAt(0);", scene);
        Assert.Contains("slot.Add(child);", scene);
        Assert.Contains("next.Dispose();", scene);
        Assert.Contains("_root.Dispose();", scene);
    }

    private static string Feature(string file) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/Features/LearningResources", file)));

    private static string Browse(string file) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/Features/Browse", file)));

    private static string Source() => File.ReadAllText(Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/App/ApplicationScene.cs")));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
