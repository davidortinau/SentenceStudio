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
        var toolbar = browser[browser.IndexOf("private static SwiftView[] ToolbarActions(", StringComparison.Ordinal)..
            browser.IndexOf("private static IEnumerable<SwiftView> FilterMenuItems(", StringComparison.Ordinal)];
        Assert.DoesNotContain("WithTintColor", toolbar);
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
        // Captain 2026-11-13: native Resource form. No suggestion popup wraps the form; Delete
        // asks in the system confirmation dialog, hidden while the delete runs.
        Assert.DoesNotContain("Views.QuickAddSuggestions(", scene);
        Assert.Contains("ResourceFormContent()", scene);
        Assert.Contains("Views.ConfirmationDialog(_fileImporter,", scene);
        Assert.Contains("_app.IsDeleteConfirmationPresented && !_app.IsResourceDeletePending", scene);
        Assert.Contains("Views.StarterBlockingOverlay(", scene);
    }

    // Captain 2026-11-13: native Resource form. Quick add is a sheet presented by the scene
    // outside the form; its rows follow the search in place and the form is not remounted.
    [Fact]
    public void QuickAddSheet_RedrawsItsRowsInPlaceWithoutRemountingForm()
    {
        var scene = Source();
        var controller = File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/App/ApplicationController.cs")));
        var update = scene[scene.IndexOf(
                "private void UpdateResourceForm(bool themeChanged = false)", StringComparison.Ordinal)
            ..scene.IndexOf("private SwiftView ResourceFormContent()", StringComparison.Ordinal)];

        Assert.Contains("internal void RefreshResourceAddWords(SourceUiStyle style) =>", controller);
        Assert.Contains("_resources?.RefreshAddWords(style);", controller);
        Assert.Contains("internal bool IsResourceSheetPresented =>", controller);
        Assert.Contains("_app.RefreshResourceAddWords(_style);", update);
        // The sheet is drawn when it opens and is presented or dismissed in place.
        Assert.Contains("var showImport = _app.IsResourceSheetPresented;", update);
        Assert.Contains("if (showImport && !importSheet.IsPresented)", update);
        Assert.Contains("Views.VStack(_app.RenderResourceSheet(_style)));", update);
        Assert.Contains("importSheet.IsPresented = showImport;", update);
        Assert.Contains("private SwiftView ResourceFormContent() => _app.RenderResourceForm(_style);",
            scene);
        Assert.DoesNotContain("RefreshQuickSuggestions", scene);
        Assert.DoesNotContain("_quickSuggestionSlot", scene);
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

        // The narrow page uses the system search field; the wide page keeps its row above.
        Assert.Contains("return BrowserPage.SingleColumn(Prefix, children, width, new BrowserPage.PageSearch(", browser);
        Assert.Contains("SearchRow(state, style, text, actions), width);", browser);
        Assert.Contains("            SearchRow(state, style, text, actions),\n            header: null, results,",
            browser);
        Assert.Contains("return BrowserPage.WithToolbar(Prefix, page, style, actions.PageWidthChanged,",
            browser);
        Assert.Contains("_resourcesSearch = Find(_resourcesSlot, BrowserPageUpdate.Resources.SearchId);",
            scene);
        Assert.Contains("_resourcesSearch = oldSearch;", scene);

        // The add sheet keeps its system search field; the form only opens it.
        Assert.Contains("inputs.OpenAddWords", quickAdd);
        Assert.Contains("_addWordsSearch = search;", coordinator);
        // Captain 2026-11-13: native Resource form. The vocabulary is a Form Section whose
        // header carries the count; the coordinator keeps the registered Section and appends a
        // quick-added word to it in place (it has no automation ID to look up).
        Assert.Contains("LearningResourceVocabularyView.AddVocabulary(sections, state,", form);
        Assert.Contains("inputs.RegisterVocabularySection(section);", vocabulary);
        Assert.Contains("sections.Add(section);", vocabulary);
        Assert.Contains("_vocabularySection = section", coordinator);
        Assert.Contains("section.Insert(insertAt++, LearningResourceVocabularyView.EditVocabularyRow(word,",
            coordinator);
        Assert.Contains("section.Text = LearningResourceVocabularyView.VocabularyHeader(",
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
