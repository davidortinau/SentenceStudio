using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSwiftUIResourceSceneContractTests
{
    [Fact]
    public void LoadingWithoutSearch_UsesReplacementUntilPopulatedSearchCanStayMounted()
    {
        var scene = Source();
        var update = scene[scene.IndexOf("private void UpdateResources(",
                StringComparison.Ordinal)
            ..scene.IndexOf("private static bool SameToolbarActions(",
                StringComparison.Ordinal)];

        Assert.Contains("var nextSearch = nextContent.Children[0];", update);
        Assert.Contains("search.Kind != \"textField\"", update);
        Assert.Contains("nextSearch.Kind != \"textField\"", update);
        Assert.Contains("nextSearch.AccessibilityIdentifier != \"native.resources.search\"",
            update);
        Assert.Contains("page.IsEnabled = nextPage.IsEnabled;", update);
        Assert.Contains("search.IsEnabled = nextSearch.IsEnabled;", update);
        Assert.Contains("Replace(_resourcesSlot, next);", update);
        Assert.Contains("!SameBrowserControls(current.Children[0], next.Children[0])",
            update);
        Assert.Contains("var controls = content.Children[1];", update);
        Assert.Contains("var filters = controls.Children[0];", update);
        Assert.Contains("var menu = filters.Children[index];", update);
        Assert.Contains("if (menu.Text != nextMenu.Text)", update);
        Assert.Contains("menu.Children[0].Children[0].Text = nextMenu.Text;",
            update);
        Assert.Contains("option.WithAccessibilitySelected(selected);", update);
        Assert.Contains("option.Children[0].Children[1].WithOpacity(selected ? 1 : 0);",
            update);
        Assert.Contains("menu.Text = nextMenu.Text;", update);
        Assert.DoesNotContain("picker.SelectedIndex", update);
        Assert.Contains("var modes = controls.Children[1];", update);
        Assert.Contains("modes.SelectedIndex = nextModes.SelectedIndex;", update);
        Assert.Contains("button.WithAccessibilityLabel(nextButton.Text);", update);
        Assert.Contains("for (var index = content.Children.Count - 1; index > 1; index--)",
            update);
        Assert.Contains("while (nextContent.Children.Count > 2)", update);
        Assert.Contains("content.Add(child);", update);
        Assert.Contains("_resourcesSearch = search;", update);
    }

    [Fact]
    public void GridListSwitch_RetainsScrollHostSearchMenusAndToggleWithLiveFilterCallbacks()
    {
        var scene = Source();
        var browser = Feature("LearningResourcesView.cs");
        var coordinator = Feature("LearningResourcesCoordinator.cs");
        var update = scene[scene.IndexOf("private void UpdateResources(",
                StringComparison.Ordinal)
            ..scene.IndexOf("private static bool SameToolbarActions(",
                StringComparison.Ordinal)];
        var page = browser[browser.IndexOf("internal static SwiftView Page(",
                StringComparison.Ordinal)
            ..browser.IndexOf("private static SwiftView ResourceListGroup(",
                StringComparison.Ordinal)];

        Assert.Contains("var page = Views.Scroll(scrollContent)", page);
        Assert.DoesNotContain("Views.List(", page);
        Assert.Contains("items.Add(search);", page);
        Assert.Contains("items.Add(controls);", page);
        Assert.Contains("var controls = Views.HStack(filters, modes)", page);
        Assert.Contains("var current = _resourcesSlot!.Children.Single();", update);
        Assert.DoesNotContain("Replace(_resourcesSlot, next);",
            update[update.IndexOf("page.IsEnabled = nextPage.IsEnabled;",
                StringComparison.Ordinal)..]);
        Assert.Contains("for (var index = content.Children.Count - 1; index > 1; index--)",
            update);
        Assert.Contains("while (nextContent.Children.Count > 2)", update);
        Assert.Contains("_style.UpdateResourceViewModeButton(button,", update);
        Assert.Contains("_presenter.Filter(type, _presenter.List.Language);",
            coordinator);
        Assert.Contains("_presenter.Filter(_presenter.List.MediaType, language);",
            coordinator);
        Assert.Contains("menu.Children[option].Text != nextMenu.Children[option].Text",
            scene);
        Assert.Contains("menu.Children[0].Children[0].Kind != \"text\"", scene);
        Assert.Contains("menu.Children[option].Children[0].Children[1].Kind != \"image\"",
            scene);
    }

    [Fact]
    public void PhoneAndWideToolbars_UpdateAllSourceActionsWithoutTreatingPrimaryAsMenu()
    {
        var scene = Source();
        Assert.Contains("!SameToolbarActions(current, next)", scene);
        Assert.Contains("current.Children.Count != next.Children.Count", scene);
        Assert.Contains("for (var actionIndex = 1; actionIndex < current.Children.Count;",
            scene);
        Assert.Contains("action.IsEnabled = nextAction.IsEnabled;", scene);
        Assert.Contains("var nextChild = nextAction.Children[index];", scene);
        Assert.Contains("child.IsEnabled = nextChild.IsEnabled;", scene);
        Assert.DoesNotContain("var menu = current.Children[1];", scene);
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

        var search = browser.IndexOf("items.Add(search);", StringComparison.Ordinal);
        var controls = browser.IndexOf("items.Add(controls);",
            StringComparison.Ordinal);
        Assert.True(search >= 0 && controls > search);
        Assert.Contains("return Views.Toolbar(style.Page(page),",
            browser);
        Assert.Contains("var search = content.Children[0];", scene);
        Assert.Contains("for (var index = content.Children.Count - 1; index > 1; index--)",
            scene);
        Assert.Contains("while (nextContent.Children.Count > 2)", scene);
        Assert.Contains("content.Add(child);", scene);
        Assert.Contains("next.Dispose();\n        _resourcesSearch = search;", scene);

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

    private static string Source() => File.ReadAllText(Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/App/ApplicationScene.cs")));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
