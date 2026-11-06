using System.Runtime.CompilerServices;
using SentenceStudio.Native.iOS.App;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeVocabularyRouteTests
{
    [Fact]
    public void WordEditorPopsOnlyAfterSaveOrCancelOnItsVocabularyStack()
    {
        // The Vocabulary list is the Library page; the word editor is pushed above it.
        string[] mounted = [ApplicationFormRoutes.VocabularyWordForm];
        Assert.False(ApplicationFormRoutes.ShouldPopVocabularyWordForm(
            formOpen: true, formMounted: true, canNavigateBack: true,
            path: mounted));
        Assert.True(ApplicationFormRoutes.ShouldPopVocabularyWordForm(
            formOpen: false, formMounted: true, canNavigateBack: true,
            path: mounted));
        Assert.False(ApplicationFormRoutes.ShouldPopVocabularyWordForm(
            formOpen: false, formMounted: true, canNavigateBack: false,
            path: mounted));
        Assert.False(ApplicationFormRoutes.ShouldPopVocabularyWordForm(
            formOpen: false, formMounted: true, canNavigateBack: true,
            path: [ApplicationFormRoutes.SkillForm]));
        Assert.False(ApplicationFormRoutes.ShouldPopVocabularyWordForm(
            formOpen: false, formMounted: true, canNavigateBack: true,
            path: [ApplicationFormRoutes.ResourceForm, ApplicationFormRoutes.VocabularyWordForm]));
    }

    [Fact]
    public void DelayedCatalogCompletion_ReprojectsMountedPhoneChoicesWithoutResettingDraft()
    {
        var coordinator = Source("Features/Vocabulary/VocabularyCoordinator.cs");
        var reload = coordinator[coordinator.IndexOf("private async Task ReloadAsync()",
                StringComparison.Ordinal)
            ..coordinator.IndexOf("private async Task OpenWordAsync(",
                StringComparison.Ordinal)];
        Assert.Contains("_catalog = catalog;", reload);
        var completion = reload[reload.IndexOf("finally", StringComparison.Ordinal)..];
        Assert.Contains("Publish(listChanged: true,", completion);
        Assert.Contains("sheetChanged: _filters?.PhoneSheetOpen == true);",
            completion);
        Assert.DoesNotContain("OpenPhoneSheet()", completion);
        Assert.DoesNotContain("ApplyPhoneSheet()", completion);

        var scene = Source("App/ApplicationScene.Vocabulary.cs");
        Assert.Contains("_renderedVocabularySheetVersion != _app.VocabularySheetVersion",
            scene);
        Assert.Contains(
            "Replace(_vocabularyFilterSlot, _app.RenderVocabularyFilters(_style));",
            scene);
        Assert.Contains(
            "_vocabularySheet.IsPresented = _app.IsVocabularyFilterPresented;",
            scene);
        var view = Source("Features/Vocabulary/VocabularyListView.cs");
        Assert.Contains("FilterFields(state, style, text, actions.Change,\n"
            + "            useDraft: true);", view);
        Assert.Contains("var words = state.Catalog.Words;", view);
        Assert.Contains("var resources = state.Catalog.Resources;", view);
        foreach (var type in new[] { "resource", "tag", "lemma", "language" })
            Assert.Contains($"fields.Add(Filter(\"{type}\"", view);
    }

    [Fact]
    public void VocabularyListRefresh_MutatesMountedScrollAndVStackButNotToolbar()
    {
        var browser = Source("Features/Vocabulary/VocabularyListView.cs");
        Assert.Contains("return Views.Toolbar(style.Page(page), toolbar)", browser);

        var scene = Source("App/ApplicationScene.Vocabulary.cs");
        var refresh = scene[scene.IndexOf(
                "private void UpdateVocabularyList(", StringComparison.Ordinal)
            ..scene.IndexOf("private void UpdateVocabularyWord(",
                StringComparison.Ordinal)];
        Assert.Contains("var current = _vocabularySlot.Children.Single();", refresh);
        Assert.Contains("current.Kind != \"toolbar\"", refresh);
        Assert.Contains("|| !SameToolbarActions(current, next)", refresh);
        Assert.Contains("current.Children[0].Kind != \"scroll\"", refresh);
        Assert.Contains("var content = current.Children[0].Children[0];", refresh);
        Assert.Contains("while (content.Children.Count > 1)", refresh);
        Assert.DoesNotContain("current.RemoveAt(", refresh);
        Assert.DoesNotContain("current.Add(", refresh);
        Assert.Contains(
            "for (var actionIndex = 1; actionIndex < current.Children.Count;",
            refresh);
        Assert.Contains("child.Text = nextChild.Text;", refresh);
        Assert.Contains("next.Dispose();", refresh);
    }

    [Fact]
    public void VocabularyFilterAndEditor_KeepNonContainerViewsInsideMutableHosts()
    {
        var scene = Source("App/ApplicationScene.Vocabulary.cs");
        Assert.Contains("_vocabularySlot = Views.VStack(_app.RenderVocabularyList(_style))",
            scene);
        Assert.Contains("_vocabularyFilterSlot = Views.VStack(", scene);
        Assert.Contains("_vocabularySheet = Views.Sheet(_vocabularySlot,", scene);
        Assert.Contains(
            "Replace(_vocabularyFilterSlot, _app.RenderVocabularyFilters(_style));",
            scene);
        Assert.Contains(
            "_vocabularySheet.IsPresented = _app.IsVocabularyFilterPresented;",
            scene);
        Assert.Contains("_vocabularyWordSlot = Views.VStack(", scene);
        // The editor page sits in its own host, inside the delete confirmation once the word
        // has loaded, so a re-render never replaces the presenting confirmation.
        Assert.Contains(
            "Replace(_vocabularyWordContent, _app.RenderVocabularyWord(_style));",
            scene);
        Assert.Contains("Replace(_vocabularyWordSlot, confirmation);", scene);
        Assert.Contains(
            "_vocabularyDeleteConfirmation.IsPresented = _app.IsVocabularyDeletePresented;",
            scene);
        Assert.DoesNotContain("_vocabularyWordContent.RemoveAt(", scene);
        Assert.Contains("FindFocusedInput(_vocabularyWordSlot)", scene);
        Assert.Contains("input.FocusRequested = true;", scene);
        Assert.DoesNotContain("_vocabularySheet.RemoveAt(", scene);
        Assert.DoesNotContain("_vocabularyWordSlot.RemoveAt(", scene);
        Assert.DoesNotContain("_vocabularyFilterSlot.RemoveAt(", scene);

        var editor = Source("Features/Vocabulary/VocabularyWordEditView.cs");
        Assert.Contains("return PageContent(fields, style);", editor);
        Assert.Contains("return Views.Toolbar(PageContent(fields, style), toolbarActions)",
            editor);
        // Source SecondaryActions at every width: Save, Find Duplicates, Delete.
        Assert.Contains(
            "Views.Menu(text(\"More\"), menuSave, menuDuplicates, menuDelete)", editor);
        // The phone More > Save follows typing like the other Save buttons (one enable rule).
        Assert.Contains("actions.RegisterMenuSave(menuSave);", editor);
        var coordinator = Source("Features/Vocabulary/VocabularyCoordinator.cs");
        Assert.Contains("RegisterMenuSave: view => _menuSaveButtons.Add(view),", coordinator);
        Assert.Contains("item.IsEnabled = enabled;", coordinator);
        Assert.Contains("return style.Page(Views.Scroll(content)", editor);
    }

    private static string Source(string relativePath) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", relativePath)));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
