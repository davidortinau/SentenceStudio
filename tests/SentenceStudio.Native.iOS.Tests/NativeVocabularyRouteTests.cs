using System.Runtime.CompilerServices;
using SentenceStudio.Native.iOS.App;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeVocabularyRouteTests
{
    [Fact]
    public void WordEditorPopsOnlyAfterSaveOrCancelOnItsVocabularyStack()
    {
        string[] mounted = [
            ApplicationFormRoutes.Vocabulary,
            ApplicationFormRoutes.VocabularyWordForm
        ];
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
            path: [ApplicationFormRoutes.Skills, ApplicationFormRoutes.SkillForm]));
    }

    [Fact]
    public void WordEditor_HidesMemoryAidsAndCannotDraftThem()
    {
        // Captain's option B (2026-10-20): global mnemonic fields have no author.
        var view = Source("Features/Vocabulary/VocabularyWordEditView.cs");
        Assert.Contains("NativeVocabulary_MemoryAidsHidden", view);
        Assert.DoesNotContain("draft.MnemonicText", view);
        Assert.DoesNotContain("draft.MnemonicImageUri", view);
        Assert.DoesNotContain("native.vocabulary.edit.mnemonic", view);
        Assert.DoesNotContain("native.vocabulary.edit.image", view);
        var coordinator = Source("Features/Vocabulary/VocabularyCoordinator.cs");
        Assert.DoesNotContain("Mnemonic", coordinator);
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
        Assert.Contains(
            "Replace(_vocabularyWordSlot, _app.RenderVocabularyWord(_style));",
            scene);
        Assert.Contains("FindFocusedInput(_vocabularyWordSlot)", scene);
        Assert.Contains("input.FocusRequested = true;", scene);
        Assert.DoesNotContain("_vocabularySheet.RemoveAt(", scene);
        Assert.DoesNotContain("_vocabularyWordSlot.RemoveAt(", scene);
        Assert.DoesNotContain("_vocabularyFilterSlot.RemoveAt(", scene);

        var editor = Source("Features/Vocabulary/VocabularyWordEditView.cs");
        Assert.Contains("return PageContent(fields, style);", editor);
        Assert.Contains("return Views.Toolbar(PageContent(fields, style), toolbarSave)",
            editor);
        Assert.Contains("return style.Page(Views.Scroll(content)", editor);
    }

    private static string Source(string relativePath) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", relativePath)));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
