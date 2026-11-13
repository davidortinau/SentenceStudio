using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.Tests;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSwiftUIResourceContractTests
{
    [Fact]
    public async Task TwoNativeTabs_ResourceJourneyCancelsAndSavesAgainstOwnerScopedStorage()
    {
        var scene = Source("App/ApplicationScene.cs");
        // iOS tab shell trial: Resources is the Library's first page.
        Assert.Equal(4, scene.Split("Views.Tab(", StringSplitOptions.None).Length - 1);
        Assert.Contains("Views.Tab(localize(\"Nav_Library\"), \"books.vertical\", library)", scene);
        Assert.Contains("\"native.library.row.resources\"", Source("App/ApplicationScene.Library.cs"));
        Assert.Contains("_libraryStack.Push(ApplicationFormRoutes.LibraryResources,",
            Source("App/ApplicationScene.Library.cs"));

        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var list = await presenter.LoadListAsync();
        Assert.Equal(NativeResourceListPhase.Populated, list.Phase);
        Assert.Single(list.Items);
        Assert.Equal(NativeResourceListPhase.NoMatches,
            presenter.Search("not-an-owned-title").Phase);
        presenter.Search("synthetic");
        Assert.Single(presenter.Filter("Vocabulary List", "Korean").Items);

        var owned = fixture.ResourceId(NativeSliceFixture.UserA);
        var foreign = fixture.ResourceId(NativeSliceFixture.UserB);
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await presenter.OpenAsync(foreign)).Phase);
        Assert.Equal(NativeResourceDetailPhase.Ready,
            (await presenter.OpenAsync(owned)).Phase);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Discarded");
        presenter.CancelEdit();
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal("Synthetic vocabulary",
                (await db.LearningResources.AsNoTracking()
                    .SingleAsync(row => row.Id == owned)).Title);
        }

        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Native Resources edit");
        presenter.ChangeDraft(NativeResourceField.Description, "Owner-scoped");
        presenter.ChangeDraft(NativeResourceField.Tags, "korean, native");
        Assert.True(await presenter.SaveAsync());
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        Assert.Equal("Native Resources edit", presenter.Detail.Resource!.Title);
        Assert.Equal(NativeResourceListPhase.NoMatches, presenter.List.Phase);
        Assert.Equal("Native Resources edit",
            Assert.Single(presenter.Search(null).Items).Title);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var reopened = fixture.Resources;
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await reopened.LoadListAsync()).Sync);
        Assert.Equal("Native Resources edit", (await reopened.OpenAsync(owned))
            .Resource!.Title);
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await reopened.OpenAsync(foreign)).Phase);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void ResourceFormsFiltersActionsAndErrorsHaveLocalizedCopy(string locale)
    {
        var values = XDocument.Load(Path.Combine(StringsPath, locale))
            .Descendants("data").ToArray();
        foreach (var key in new[]
        {
            "Resources_Title", "Resources_AllTypes", "Resources_AllLanguages",
            "Resources_SearchPlaceholder", "Resources_GridView", "Resources_ListView",
            "Resources_AddResource", "Resources_AddFirstResource",
            "Resources_CreateStarter", "Resources_CreatingStarter",
            "ResourceAdd_Title", "ResourceAdd_BasicInfo", "ResourceAdd_MediaContent",
            "ResourceAdd_DescriptionPlaceholder",
            "ResourceAdd_TranscriptPlaceholder",
            "ResourceAdd_TranslationPlaceholder",
            "ResourceAdd_VocabPlaceholder", "ResourceEdit_VocabPlaceholder",
            "ResourceAdd_ImportVocabulary", "ResourceAdd_ChooseFile",
            "ResourceAdd_TitleRequired", "ResourceEdit_Title", "ResourceEdit_Description",
            "ResourceEdit_Tags", "ResourceEdit_MediaType", "ResourceEdit_Language",
            "ResourceEdit_MediaUrl", "ResourceEdit_Transcript", "ResourceEdit_Translation",
            "ResourceEdit_VocabularySection", "ResourceEdit_QuickAddLabel",
            "ResourceEdit_ChooseFile", "ResourceEdit_GenerateFromTranscript",
            "ResourceEdit_DeleteMessage", "ResourceEdit_AutoUpdated",
            "NativeResources_Empty", "NativeResources_NoMatches",
            "NativeResources_LoadError", "NativeResources_SaveError",
            "NativeResources_SaveRefused", "NativeResources_SavedReloadError",
            "NativeResources_Conflict", "NativeResources_ConflictTitle",
            "NativeResources_KeepEditing", "NativeResources_ReloadDiscard",
            "NativeResources_ReloadError", "NativeResources_SaveInvalid",
            "NativeResources_SyncUnconfirmed", "NativeResources_SyncConfirmed",
            "NativeResources_TypeFilter", "NativeResources_LanguageFilter",
            "NativeResources_FileTooLarge", "NativeResources_FileReadError",
            "NativeResources_UnsupportedFile",
            "NativeResources_SetTargetLanguage",
            "NativeResources_SaveLanguageBeforeQuickAdd",
            "VocabWordEdit_Unknown", "Vocabulary_FilterTypeWord",
            "Vocabulary_FilterTypePhrase", "Vocabulary_FilterTypeSentence",
            "NativeResources_LexicalIdiom",
        })
            Assert.True(values.Any(data =>
                data.Attribute("name")?.Value == key
                && !string.IsNullOrWhiteSpace(data.Element("value")?.Value)),
                $"{locale} lacks copy for {key}");
    }

    [Theory]
    [InlineData("More")]
    [InlineData("Yes")]
    [InlineData("No")]
    public void NativeText_ResolvesIdenticalEnglishTranslation(string key) =>
        Assert.Equal(key, NativeLocalizedText.Get(key,
            CultureInfo.GetCultureInfo("en-US")));

    [Fact]
    public void NativeText_UsesSharedCultureFallbackAndRejectsAbsentKeys()
    {
        var korean = XDocument.Load(Path.Combine(
            StringsPath, "AppResources.ko.resx"));
        var expected = korean.Descendants("data")
            .Single(item => item.Attribute("name")?.Value == "More")
            .Element("value")!.Value;
        Assert.Equal(expected,
            NativeLocalizedText.Get("More", CultureInfo.GetCultureInfo("ko-KR")));
        Assert.Equal("More",
            NativeLocalizedText.Get("More", CultureInfo.GetCultureInfo("fr-FR")));
        var missing = Assert.Throws<InvalidOperationException>(() =>
            NativeLocalizedText.Get("NativeIosMissingKeyRegression",
                CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Missing shared localization resource: NativeIosMissingKeyRegression",
            missing.Message);
    }

    [Fact]
    public void RealSwiftUIHead_UsesCheckedSharedLocalizationForAuthAndResourceViews()
    {
        var app = Source("App/ApplicationController.cs");
        var auth = Source("Features/Authentication/SignInView.cs");
        var resources = Source("Features/LearningResources/LearningResourceView.cs");
        Assert.Contains("NativeLocalizedText.Get(key,", app);
        Assert.Contains("SignInView.Page(", app);
        Assert.Contains("LearningResourceView.Page(", Source(
            "Features/LearningResources/LearningResourcesCoordinator.cs"));
        Assert.Contains("Func<string, string> text", auth);
        Assert.Contains("Func<string, string> text", resources);
        Assert.DoesNotContain("localized == key", app);
    }

    [Fact]
    public void SourceResourceKindsAndSourceCatalogFilterDefaultsAreStillVisible()
    {
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var presenter = NativeSource("NativeResourcePresenter.cs");
        Assert.Contains("_ => LexicalUnitType.Word", vocabulary);
        Assert.Contains("LexicalUnitType.Phrase => 1", vocabulary);
        Assert.Contains("LexicalUnitType.Sentence => 2", vocabulary);
        Assert.DoesNotContain("NativeResources_LexicalIdiom", vocabulary);
        Assert.DoesNotContain("VocabWordEdit_Unknown", vocabulary);
        Assert.Contains("text(\"Resources_AllTypes\")", browser);
        Assert.Contains("text(\"Resources_AllLanguages\")", browser);
        Assert.Contains("Constants.MediaTypes.ToImmutableArray()", presenter);
        Assert.Contains("Constants.Languages.ToImmutableArray()", presenter);
    }

    /// <summary>
    /// Captain 2026-11-13: native Resource form. Bulk import rows keep the visible comma/tab
    /// delimiter (a segmented Picker) and a registered Import; a word is removed by swiping its
    /// row (a red, destructive Remove), replacing the trailing x button.
    /// </summary>
    [Fact]
    public void BulkVocabularyShowsVisibleDelimiterAndSwipeToRemove()
    {
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var bulk = form[form.IndexOf("internal static List<SwiftView> ImportRows(",
                StringComparison.Ordinal)
            ..form.IndexOf("internal static SwiftView EditVocabularyRow(", StringComparison.Ordinal)];
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Segmented)", bulk);
        Assert.Contains("inputs.DelimiterChanged(index == 1 ? \"tab\" : \"comma\")", bulk);
        Assert.Contains("inputs.RegisterImport(import);", bulk);
        Assert.Contains("import.IsEnabled = !busy && !string.IsNullOrWhiteSpace(inputs.PasteText);",
            bulk);
        Assert.Contains("inputs.ChooseFile", bulk);

        var remove = form[form.IndexOf("private static SwiftView RemoveAction(",
                StringComparison.Ordinal)..];
        Assert.Contains("Views.Button(Views.Label(text(\"ResourceEdit_RemoveWord\"), \"trash\"),", remove);
        Assert.Contains("() => removeWord(id)", remove);
        Assert.Contains(".WithButtonRole(SwiftButtonRole.Destructive)", remove);
        Assert.Contains(".WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)",
            remove);
        Assert.DoesNotContain("Views.Image(\"xmark\")", form);
    }

    /// <summary>
    /// Captain 2026-11-13: native Resource form. A new resource's staged words are rows of
    /// their own Section (header: the count) instead of the five-column table: the term and
    /// meaning, the tags under them, the type as a menu Picker, swipe to remove; never editable
    /// text fields. An existing resource's words keep the bold term and secondary meaning.
    /// </summary>
    [Fact]
    public void ResourceAddStagedWords_AreSectionRowsWithATypeMenu()
    {
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var add = form[form.IndexOf("internal static void AddVocabulary(", StringComparison.Ordinal)
            ..form.IndexOf("internal static string VocabularyHeader(", StringComparison.Ordinal)];
        Assert.Contains("if (state.Vocabulary.Count > 0)", add);
        Assert.Contains("var staged = Views.Section(StagedHeader(state.Vocabulary.Count, text),", add);
        Assert.Contains("StagedRow(word, busy, actions, style, text)", add);
        Assert.Contains("inputs.RegisterStagedGrid(staged);", add);
        Assert.Contains("text(\"ResourceAdd_VocabWordsAdded\"), count)", form);

        var staged = form[form.IndexOf("internal static SwiftView StagedRow(", StringComparison.Ordinal)
            ..form.IndexOf("private static SwiftView Terms(", StringComparison.Ordinal)];
        Assert.Contains("string.IsNullOrWhiteSpace(word.Tags)", staged);
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Menu)", staged);
        Assert.Contains(".WithAccessibilityLabel(text(\"Import_ColType\"))", staged);
        Assert.Contains("actions.SetWordType(id,", staged);
        Assert.Contains("Views.SwipeRow(content, [RemoveAction(id, actions.RemoveWord, style, text)])",
            staged);
        Assert.Contains("if (busy)", staged);
        Assert.DoesNotContain("Views.HorizontalGrid(", form);
        Assert.DoesNotContain("Views.TextField(word.TargetTerm", form);
        Assert.DoesNotContain("Views.TextField(word.NativeTerm", form);

        var terms = form[form.IndexOf("private static SwiftView Terms(", StringComparison.Ordinal)..];
        Assert.Contains("string.IsNullOrEmpty(word.NativeTerm)", terms);
        Assert.Contains("SourceTextRole.Body1Bold,\n                strongElement: true", terms);
        Assert.Contains("SourceTextRole.Body1, SourceTextTone.Secondary", terms);
    }

    [Fact]
    public void StagedTypeAndRemove_PreserveMountedSectionUntilItsLastRow()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var type = coordinator[coordinator.IndexOf("SetWordType: (id, kind) =>",
                StringComparison.Ordinal)
            ..coordinator.IndexOf("ImportText: () =>", StringComparison.Ordinal)];
        Assert.Contains("_presenter.SetStagedLexicalUnitType(id, kind)", type);
        Assert.Contains("Publish(formChanged: false);", type);
        Assert.DoesNotContain("Publish();", type);

        // Captain 2026-11-13: native Resource form. The staged words' Section drops the row
        // in place and its header takes the new count.
        var remove = coordinator[coordinator.IndexOf(
                "private void RemoveVocabulary(string id)", StringComparison.Ordinal)
            ..coordinator.IndexOf("private bool TryRemoveWordRow(string id)",
                StringComparison.Ordinal)];
        Assert.Contains("if (_creating && _stagedGrid is { } section", remove);
        Assert.Contains("for (var index = 0; index < section.Children.Count; index++)", remove);
        Assert.Contains("section.RemoveAt(index);", remove);
        Assert.Contains("detached.Dispose();", remove);
        Assert.True(remove.IndexOf("section.RemoveAt(index);", StringComparison.Ordinal)
            < remove.IndexOf("detached.Dispose();", StringComparison.Ordinal));
        Assert.Contains("section.Text = LearningResourceVocabularyView.StagedHeader(", remove);
        Assert.Contains("Publish(formChanged: false);", remove);
        Assert.Contains("Publish();", remove);
        Assert.Contains("inputs.RegisterStagedGrid(staged);", form);

        // An existing resource's removed word leaves its Section in place too (the form keeps
        // its scroll position), and the form is not redrawn before the removal.
        var inPlace = coordinator[coordinator.IndexOf("private bool TryRemoveWordRow(string id)",
                StringComparison.Ordinal)
            ..coordinator.IndexOf("private async Task MutateVocabularyAsync(", StringComparison.Ordinal)];
        Assert.Contains("section.RemoveAt(index);", inPlace);
        Assert.Contains("section.Add(LearningResourceVocabularyView.EmptyRow(style, _text));", inPlace);
        Assert.Contains("section.Text = LearningResourceVocabularyView.VocabularyHeader(count, _text);",
            inPlace);
        // In place only while no status or error row shows (review 2026-11-13); a refused
        // swipe redraws what is true.
        Assert.Contains("applyInPlace: inPlace ? () => TryRemoveWordRow(id) : null", remove);
        Assert.Contains("var inPlace = _formStatus is null && _formErrorKey is null;", remove);
        Assert.Contains("if (applyInPlace is null && !fromSheet)", coordinator);
    }

    [Fact]
    public void ResourcesUseLazySwiftUIGridAndContiguousListGroupWithWholeRowActivation()
    {
        var resources = Source("Features/LearningResources/LearningResourcesView.cs");
        var row = Source("Features/LearningResources/LearningResourceRowView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var presenter = NativeSource("NativeResourcePresenter.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("return SourceUiLayout.ResourceColumns(width);", resources);
        // Captain 2026-11-12: list mode is a native List of rows (icons kept) with swipe actions:
        // Delete (not smart resources, confirmed) and Study; the grid stays a lazy grid.
        Assert.Contains("children.AddRange(resources.Select(item => ListRow(item, text, actions, style, width)));", resources);
        Assert.Contains("return Views.SwipeRow(row, trailing, leading)", resources);
        Assert.Contains(".WithButtonRole(SwiftButtonRole.Destructive)", resources);
        Assert.Contains("AsNativeList(state) ? style : null);", resources);
        Assert.Contains("asList: AsNativeList(state));", resources);
        Assert.DoesNotContain("Views.List(", resources);
        Assert.Contains("Views.Button(label, () => open(resource.Id))", row);
        Assert.Contains("SourceUiLayout.ShowResourceListMetadata(width ?? style.ViewportWidth)",
            row);
        Assert.Equal(1, row.Split(".WithLineLimit(1)",
            StringSplitOptions.None).Length - 1);
        Assert.Contains(".WithMinimumHeight(list ? SourceUiLayout.MinimumTouchHeight : 76)",
            row);
        Assert.DoesNotContain("style.BorderedCard(label", row);
        Assert.Contains(".WithAccessibilityLabel($\"{title}, {metadata}\")", row);
        Assert.Contains("WithAccessibilityIdentifier($\"{NativeAccess.ResourceRow}.{resource.Id}\")",
            row);
        Assert.Contains("NativeResourceListPhase.NoMatches", resources);
        Assert.Contains("NativeResourceListPhase.Empty", resources);
        Assert.Contains("Resources_AddFirstResource", resources);
        Assert.Contains("Resources_CreateStarter", resources);
        Assert.Contains("ToggleViewMode() => ListChanged(_presenter.SetViewMode(",
            Source("Features/LearningResources/LearningResourcesCoordinator.ListOptions.cs"));
        Assert.Contains("private const string ViewModeKey = \"resources-view-mode\"", presenter);
        Assert.Contains("Views.TextField(", resources);
        Assert.Contains("Search: Search,", coordinator);
        // Resources is the Library's first page: the split view's middle column.
        var library = Source("App/ApplicationScene.Library.cs");
        Assert.Contains("_librarySplit = new LibrarySplitNavigation(_libraryRoot,", library);
        Assert.Contains("_resourcesSlot = Views.VStack(_app.RenderResources(_style))", library);
        Assert.DoesNotContain("Views.Searchable(_resourcesSlot,", scene + library);
        // One freshness path: the list is read again when its source moved since its last
        // successful read (a download applied rows or a local write), or it is not shown.
        Assert.Contains("internal void ReadListIfStale()", coordinator);
        Assert.Contains("var version = _presenter.ListSourceVersion;", coordinator);
        Assert.Contains("_readVersion = null;", coordinator);
        Assert.Contains("return _readVersion == current && _presenter.List is {", coordinator);
        Assert.Contains("refreshInPlace: true", coordinator);
        Assert.DoesNotContain("_rereadRequested", coordinator);
        Assert.DoesNotContain("TickCount", coordinator);
        var app = Source("App/ApplicationController.cs");
        Assert.DoesNotContain("MarkListStale", app + Source("App/ApplicationController.Sam.cs"));
        Assert.DoesNotContain("OnResourcesAppeared", app);
        Assert.Contains("if (state.Phase is NativeResourceListPhase.Loading or NativeResourceListPhase.Idle)",
            resources);
        Assert.Contains("Resources_NoResourcesFound", resources);
        Assert.Contains("native.resources.empty.actions", resources);
        Assert.DoesNotContain("native.resources.sync.menu", coordinator);
        Assert.DoesNotContain("native.resources.form.sync.menu", coordinator);
    }

    [Fact]
    public void ResourceListRows_KeepPhoneTitleAndDateWithoutGridMetadataOrCardChrome()
    {
        var row = Source("Features/LearningResources/LearningResourceRowView.cs");
        // A list row follows its own column's width: the type and language beside the title
        // where they fit, otherwise under it (a split view's narrow list column).
        var list = row[row.IndexOf(
                "if (list && SourceUiLayout.ShowResourceListMetadata(width ?? style.ViewportWidth))",
                StringComparison.Ordinal)
            ..row.IndexOf("label.WithFlexibleWidth();", StringComparison.Ordinal)];

        Assert.Contains("label = Views.HStack(\n                icon,\n                name,", list);
        Assert.Contains("style.Text(Views.Text(media), SourceTextRole.Small,", list);
        Assert.Contains("label = Views.HStack(icon,\n                Views.VStack(\n                    name,", list);
        Assert.Contains("double? width = null,", row);
        Assert.Contains("bool nativeRow = false)", row);
        Assert.Contains("list ? SourceTextRole.ListTitle : SourceTextRole.Title3", row);
        Assert.Contains("sourceHeading: !list)\n            .WithLineLimit(1)", row);
        Assert.Equal(1, row.Split(".WithLineLimit(",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("label.WithPadding(8, SourceUiLayout.CardInset,", row);
        Assert.Contains("style.Card(label);", row);
        Assert.DoesNotContain("style.BorderedCard(label", row);
        Assert.Contains(".WithMinimumHeight(list ? SourceUiLayout.MinimumTouchHeight : 76)",
            row);
    }

    [Fact]
    public void LocalTimestampsAndCompleteTranscriptRemainUntruncated()
    {
        var resources = Source("Features/LearningResources/LearningResourceView.cs");
        var row = Source("Features/LearningResources/LearningResourceRowView.cs");
        var fields = Source("Features/LearningResources/LearningResourceFieldsView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        Assert.Contains("DateFormatter.ListDate(resource.CreatedAt,", row);
        Assert.Contains("DateFormatter.DetailCreatedDate(", resources);
        Assert.Contains("DateFormatter.DetailUpdatedDate(", resources);
        Assert.Contains("Transcript = draft?.Transcript ?? resource?.Transcript",
            coordinator);
        Assert.Contains("Translation = draft?.Translation ?? resource?.Translation",
            coordinator);
        Assert.Contains("Views.TextEditor(value ?? string.Empty,", fields);
        // A read-only area (a smart resource) is its text, not a disabled input (Captain 2026-11-12);
        // Captain 2026-11-13 (native Resource form): the text is the row of the field's Section.
        Assert.Contains("Views.Section(label, readOnly\n                ? Views.Text(value ?? string.Empty)", fields);
        Assert.DoesNotContain("TranscriptPreview", resources);
        Assert.DoesNotContain("TranslationPreview", resources);
        Assert.DoesNotContain("Substring(0, 240)", resources);
        Assert.DoesNotContain(".MaxLength =", resources);
    }

    [Fact]
    public void ResourceTextEditorsUseOnlyTheFiveSourcePlaceholdersOnTheSameNativeInput()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var fields = Source("Features/LearningResources/LearningResourceFieldsView.cs");
        foreach (var key in new[]
        {
            "ResourceAdd_DescriptionPlaceholder",
            "ResourceAdd_TranscriptPlaceholder",
            "ResourceAdd_TranslationPlaceholder",
        })
            Assert.Contains($"\"{key}\"", form);
        Assert.Contains("\"ResourceAdd_VocabPlaceholder\"", vocabulary);
        Assert.Contains("\"ResourceEdit_VocabPlaceholder\"", vocabulary);
        Assert.Contains("input.WithTextEditorPlaceholder(placeholder)", fields);
        Assert.Contains(".WithPlaceholderColor(style.Appearance.TertiaryText.Light,",
            fields);
        Assert.Contains("style.Appearance.TertiaryText.Dark);", fields);
        Assert.Contains(".WithTextEditorScrollBackgroundHidden();", fields);
        Assert.Equal(1, fields.Split(".WithTextEditorScrollBackgroundHidden()",
            StringSplitOptions.None).Length - 1);
        // Captain 2026-11-13: native Resource form. The editor is the Section's row itself, at
        // least the given number of lines high (no input box around it).
        Assert.Contains(".WithMinimumHeight(AreaHeight(style, rows))", fields);
        Assert.DoesNotContain("style.Input(input)", fields);
        Assert.Contains("if (placeholder is not null)", fields);
        Assert.DoesNotContain("native.resources.form.vocabulary.example", vocabulary);

        var edit = form[form.IndexOf("if (!creating && !readOnly)\n        {\n            sections.Add(LearningResourceFieldsView.Area(style, text(\"ResourceEdit_Transcript\"),",
                StringComparison.Ordinal)
            ..form.IndexOf("LearningResourceVocabularyView.AddVocabulary(sections,",
                StringComparison.Ordinal)];
        Assert.Contains("ResourceEdit_Transcript", edit);
        Assert.Contains("ResourceEdit_Translation", edit);
        Assert.DoesNotContain("placeholder:", edit);
        // A smart resource hides these Sections, so they are always editable here.
        Assert.Contains("readOnly: false, rows: 6", edit);
    }

    // Captain 2026-11-13: native Resource form (a grouped Form, not cards in a ScrollView).
    [Fact]
    public void FullResourceCreateEditAndSmartReadOnlyUseABoundNativeForm()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        var fields = Source("Features/LearningResources/LearningResourceFieldsView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var scene = Source("App/ApplicationScene.cs");
        foreach (var field in new[]
        {
            "Title", "Description", "MediaType", "Language",
            "MediaUrl", "Tags", "Transcript", "Translation"
        })
            Assert.Contains($"nameof(LearningResourceViewState.{field})", form);
        Assert.Contains("style.GroupedForm(Views.Form(sections.ToArray())", form);
        Assert.DoesNotContain("style.Card(", form);
        Assert.Contains("Views.Picker(", fields);
        Assert.Contains("Views.LabeledContent(label,", fields);
        Assert.Contains("var readOnly = state.IsSmartResource;", form);
        Assert.Contains("state.IsSmartResource", form);
        Assert.Contains("ResourceEdit_AutoUpdated", form);
        Assert.Contains("_presenter.BeginCreate()", coordinator);
        Assert.Contains("_presenter.BeginEdit()", coordinator);
        Assert.Contains("_presenter.SaveAsync(_lifetime.Token)", coordinator);
        Assert.Contains("_formStatus = _text(\"ResourceEdit_SavedSuccess\")", coordinator);
        Assert.Contains("_presenter.CancelEdit();", coordinator);
        Assert.Contains("_libraryStack.Push(\"resource-form\",", scene);
        Assert.Contains("_conflictAlert.WithTabBarHidden()", scene);
    }

    // Captain 2026-11-13: native Resource form (Form Sections replace the cards).
    [Fact]
    public void NativeFormResourceEditorRetainsLiveInputsFocusAndQuickAddMutations()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        var fields = Source("Features/LearningResources/LearningResourceFieldsView.cs");
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var quickAdd = Source("Features/LearningResources/LearningResourceQuickAddView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var scene = Source("App/ApplicationScene.cs");

        // The label sits beside the field, so the field has no prompt unless one is given.
        Assert.Contains("Views.TextField(placeholder ?? string.Empty,", fields);
        Assert.Contains("value ?? string.Empty, changed)", fields);
        Assert.Contains("Views.TextEditor(value ?? string.Empty,", fields);
        Assert.Contains("Views.TextEditor(value ?? string.Empty, changed)\n            .WithTextEditorScrollBackgroundHidden();",
            fields);
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Menu)", fields);
        // Quick add is an "Add vocabulary" row that opens the add sheet (its system search field
        // lives in the sheet, outside the form, so form updates never take its focus).
        Assert.Contains("inputs.OpenAddWords", quickAdd);
        Assert.Contains("native.resources.edit.quick.add", quickAdd);
        Assert.Contains("search.FocusRequested = true;", coordinator);
        Assert.Contains("inputs.RegisterImport(import);", vocabulary);
        // The toolbar's plain Save is the form's only Save (Captain 2026-11-10).
        Assert.Contains("registerSave(save);", form);

        Assert.Contains("inputs.RegisterVocabularySection(section);", vocabulary);
        Assert.Contains("section.Insert(insertAt++, LearningResourceVocabularyView.EditVocabularyRow(word,",
            coordinator);
        // The bulk import and add sheets sit outside the form (so a form update never redraws
        // them), the file chooser outside the sheet, and the delete dialog outside that.
        Assert.Contains("_importSheet = Views.Sheet(_formSlot, _importSheetSlot,", scene);
        Assert.Contains("_app.OnResourceSheetDismissed);", scene);
        Assert.Contains("Views.FileImporter(_importSheet,", scene);
        Assert.Contains("Views.ConfirmationDialog(_fileImporter,", scene);
        Assert.Contains("Views.Alert(_deleteConfirmation,", scene);
        Assert.DoesNotContain("Views.QuickAddSuggestions(", scene);
    }

    [Fact]
    public void ConflictPreservesDraftAndRequiresExplicitNativeSwiftUIReload()
    {
        var scene = Source("App/ApplicationScene.cs");
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        Assert.Contains("Views.Alert(_deleteConfirmation,", scene);
        Assert.Contains("NativeResources_ConflictTitle", scene);
        Assert.Contains("NativeResources_KeepEditing", scene);
        Assert.Contains("NativeResources_ReloadDiscard", scene);
        Assert.Contains("state.Phase == NativeResourceDetailPhase.Conflict", form);
        Assert.Contains("NativeAccess.ResourceConflict", form);
        Assert.Contains("WithAccessibilityIdentifier(NativeAccess.ResourceReload)",
            form);
        Assert.Contains("_presenter.ReloadDraftAsync(_lifetime.Token)", coordinator);
        Assert.Contains("_presenter.Detail.Draft is null", coordinator);
        Assert.DoesNotContain("CaptureDraft()", coordinator);
    }

    [Fact]
    public void SyncSingleFlightPreservesOpenDraftAndRetryReflectsOwnerConfirmedState()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        Assert.Contains("NativeResourceSyncStatus.Describe(", browser);
        Assert.DoesNotContain("native.resources.sync.menu", browser);
        Assert.Contains("Views.Button(text(NativeText.Retry), actions.Reload)", browser);
        Assert.Contains("Views.Button(text(NativeText.Retry), actions.Retry)", form);
        Assert.Contains("private readonly NativeResourceSyncAction _syncAction = new();",
            coordinator);
        Assert.Contains("_syncAction.RunAsync(SynchronizeAndRefreshAsync)", coordinator);
        Assert.Contains("_presenter.RefreshAfterSyncAsync(synchronization, _lifetime.Token)",
            coordinator);
        Assert.Contains("_presenter.IsLatestSynchronization(synchronization)",
            coordinator);
        Assert.Contains("&& _presenter.Detail.Draft is null", coordinator);
    }

    [Fact]
    public void EditFormNeverShowsPassiveSyncForPendingUnconfirmedOrConfirmed()
    {
        var views = Source("Features/LearningResources/LearningResourceView.cs");
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var edit = views[views.IndexOf("internal static SwiftView Page(",
                StringComparison.Ordinal)
            ..views.IndexOf("internal static SwiftView WithToolbar(",
                StringComparison.Ordinal)];
        Assert.DoesNotContain("state.Sync", edit);
        Assert.DoesNotContain("NativeResourceSyncPhase.", edit);
        Assert.DoesNotContain("NativeResourceSyncStatus.Describe(", edit);
        Assert.DoesNotContain("native.resources.form.sync", edit);
        Assert.Contains("var syncStatus = NativeResourceSyncStatus.Describe(",
            browser);
        Assert.Contains("native.resources.sync.status", browser);
        Assert.Contains("if (state.ErrorKey is { } error", edit);
        Assert.Contains("native.resources.form.error", edit);
        Assert.Contains("native.resources.form.saving", edit);
        Assert.Contains("native.resources.form.retry", edit);
        Assert.Contains("NativeResources_SaveInvalid", edit);
        Assert.Contains("NativeAccess.ResourceReload", edit);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void PendingAndFailedSyncCopyStayTruthfulInBothLocales(string locale)
    {
        var strings = XDocument.Load(Path.Combine(StringsPath, locale))
            .Descendants("data").ToArray();
        string Text(string key) => Assert.Single(strings,
            item => item.Attribute("name")?.Value == key).Element("value")!.Value;
        var pending = $"{Text("NativeResources_Sync")}: {Text("DebugHealth_Pending")}";
        Assert.DoesNotContain(Text("DebugHealth_CoreSync"), pending);
        var error = Text("NativeResources_SyncUnconfirmed");
        Assert.Equal($"{pending}\n{error}",
            NativeResourceSyncStatus.Describe(NativeResourceSyncPhase.Pending,
                "NativeResources_SyncUnconfirmed", Text));
        Assert.Equal(pending, NativeResourceSyncStatus.Describe(
            NativeResourceSyncPhase.Pending, null, Text));
        Assert.Equal(Text("NativeResources_SyncConfirmed"),
            NativeResourceSyncStatus.Describe(
                NativeResourceSyncPhase.Confirmed, null, Text));
        Assert.Equal(error, NativeResourceSyncStatus.Describe(
            NativeResourceSyncPhase.Unconfirmed, "NativeResources_SyncUnconfirmed",
            Text));
    }

    [Fact]
    public void NativePopAndDisposeInvalidateOutstandingResourceDetailAndSync()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var scene = Source("App/ApplicationScene.Library.cs");
        Assert.Contains("_app.OnResourcesFormPopped()", scene);
        Assert.Contains("if (!_formOpen || IsNavigationBlocked)", coordinator);
        Assert.Contains("_presenter.CancelEdit();", coordinator);
        Assert.Contains("CloseForm(notifyNavigation: false, publish: false);",
            coordinator);
        Assert.Contains("if (publish)\n            Publish(formChanged: false);",
            coordinator);
        Assert.Contains("_presenter.InvalidateDetail();", coordinator);
        Assert.Contains("_presenter.InvalidateSync();", coordinator);
        Assert.Contains("_quickSearch?.Cancel();", coordinator);
        Assert.Contains("Interlocked.Increment(ref _quickSearchVersion)", coordinator);
        Assert.Contains("_libraryStack.CanNavigateBack = !_app.NavigationBlocked",
            Source("App/ApplicationScene.cs"));
    }

    [Fact]
    public void CommittedCreateReloadFailureReopensExactlyTheCommittedResource()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        Assert.Contains("ErrorKey: \"NativeResources_SavedReloadError\"", coordinator);
        Assert.Contains("_presenter.Detail.CommittedResourceId", coordinator);
        Assert.Contains("CloseForm();", coordinator);
        Assert.Contains("await OpenAsync(committedId);", coordinator);
        Assert.Contains("_listFeedback = _text(\"NativeResources_SavedReloadError\")",
            coordinator);
        Assert.DoesNotContain("BeginCreate();\n            await OpenAsync(committedId)", coordinator);
    }

    [Fact]
    public void VocabularyGenerationImportQuickAddAndUnlinkRemainOwnerScoped()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var quickAdd = Source("Features/LearningResources/LearningResourceQuickAddView.cs");
        var services = Source("NativeServices.cs");
        Assert.Contains("services.AddOwnerBoundNativeAiGateway(endpoint);", services);
        Assert.DoesNotContain("AddHttpClient<IAiApiClient, AiApiClient>", services);
        Assert.Contains("_presenter.StageVocabularyFromText(", coordinator);
        Assert.Contains("_presenter.StageVocabularyFromFileAsync(", coordinator);
        Assert.Contains("_presenter.SetStagedLexicalUnitType(", coordinator);
        Assert.Contains("_presenter.RemoveStagedVocabulary(", coordinator);
        Assert.Contains("_presenter.SearchVocabularyAsync(", coordinator);
        Assert.Contains("_presenter.AttachExistingVocabularyAsync(", coordinator);
        Assert.Contains("_presenter.CreateNewVocabularyAsync(", coordinator);
        Assert.Contains("_presenter.RemoveVocabularyAsync(", coordinator);
        Assert.Contains("_presenter.ImportVocabularyFromTextAsync(", coordinator);
        Assert.Contains("_presenter.ImportVocabularyFromFileAsync(", coordinator);
        Assert.Contains("_presenter.GenerateVocabularyAsync(_lifetime.Token)",
            coordinator);
        Assert.Contains("result.GeneratedTotal, result.CreatedCount,", coordinator);
        Assert.Contains("result.ExistingLinkedCount", coordinator);
        Assert.Contains("ResourceEdit_GenerateSuccess", coordinator);
        // Captain 2026-11-13: native Resource form. A new resource imports in place; an
        // existing one opens the bulk import sheet (never on a smart resource).
        Assert.Contains("ImportRows(state, creating: true, busy, actions, inputs, style, text,", form);
        Assert.Contains("inputs.ToggleBulk)", form);
        Assert.Contains("state.QuickAddResults", quickAdd);
        Assert.Contains("native.resources.form.vocabulary.remove.{id}", form);
        // The add sheet (Captain 2026-11-13): the system search field over the learner's
        // matching words; a match is added with the same owner-scoped attach, a new word with
        // the owner-scoped create; one sheet at a time.
        Assert.Contains("text(\"ResourceEdit_QuickAddPlaceholder\")", coordinator);
        Assert.Contains("Views.Searchable(style.ThemedList(list),", coordinator);
        Assert.Contains("id => Run(() => AttachVocabularyAsync(id)),", coordinator);
        Assert.Contains("native.resources.edit.quick.option.{id}", quickAdd);
        Assert.Contains("native.resources.edit.quick.new", quickAdd);
        Assert.Contains("native.resources.edit.quick.listbox", coordinator);
        Assert.Contains("internal bool IsSheetPresented => _formOpen && !_creating && (_bulkExpanded || IsAddWordsOpen);",
            coordinator);
        Assert.Contains("if (!_formOpen || _creating || IsNavigationBlocked || _bulkExpanded || _addWordsOpen",
            coordinator);
        // A sheet the app closed blocks the next until its dismissal arrives (review 2026-11-13).
        Assert.Contains("|| _sheetClosing)", coordinator);
        Assert.Contains("var closedByApp = _sheetClosing;", coordinator);
        Assert.Contains("if (!HasQuickAddField || IsNavigationBlocked || _bulkExpanded || _addWordsOpen",
            coordinator);
        Assert.Contains("internal bool HasQuickAddField", coordinator);
        Assert.Contains("Views.IndeterminateProgress(text(\"Common_Loading\"))",
            quickAdd);
        Assert.DoesNotContain("RenderQuickAddSuggestions", coordinator);
        Assert.DoesNotContain("_quickOptionNodes", coordinator);
        Assert.True(form.IndexOf("rows.AddRange(LearningResourceQuickAddView.FormRows(",
                StringComparison.Ordinal)
            < form.IndexOf("rows.AddRange(state.Vocabulary.Take(", StringComparison.Ordinal));
        Assert.Contains("ResourceAdd_VocabWordsAdded", form);
    }

    [Fact]
    public void ResourceMutationsReadTheCurrentTextInsteadOfCapturedRenderedInputs()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var quickAdd = Source("Features/LearningResources/LearningResourceQuickAddView.cs");
        Assert.Contains("_quickQuery.Trim(), _quickNativeTerm.Trim()", coordinator);
        Assert.Contains("ImportVocabularyTextAsync(_pasteText, _delimiter)", coordinator);
        // Captain 2026-11-13: native Resource form. Add (a new word) is an action row of the add
        // sheet, which reads the current query and meaning when tapped.
        Assert.Contains("() => Run(() => CreateVocabularyAsync(\n                _quickQuery.Trim(), _quickNativeTerm.Trim()))",
            coordinator);
        Assert.Contains("create)", quickAdd);
        Assert.Contains("actions.ImportText)", form);
        Assert.Contains("PasteTextChanged: value =>", coordinator);
        Assert.Contains("UpdateImportButtons();", coordinator);
        Assert.Contains("inputs.RegisterImport(import);", form);
        // The sheet reads the current paste text, not the form's captured inputs.
        Assert.Contains("formInputs with { PasteText = _pasteText, Delimiter = _delimiter }",
            coordinator);
        Assert.Contains("_importSheetButton = import;", coordinator);
        Assert.DoesNotContain("inputs.QuickQuery", quickAdd);
        Assert.DoesNotContain("actions.ImportText(inputs.PasteText", form);
    }

    [Fact]
    public void QuickAddQueryRedrawsSheetRowsInPlaceWithoutRemountingTheSearchField()
    {
        // Captain 2026-11-13: native Resource form. Quick add is a sheet with the system search
        // field; a search bumps the suggestions revision and publishes without redrawing the
        // form, and the scene redraws only the sheet's list rows (the field keeps its focus).
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var views = Source("Features/LearningResources/LearningResourceQuickAddView.cs");
        var scene = Source("App/ApplicationScene.cs");
        var search = coordinator[coordinator.IndexOf(
                "private async Task SearchVocabularyAsync(", StringComparison.Ordinal)
            ..coordinator.IndexOf("private Task AttachVocabularyAsync(",
                StringComparison.Ordinal)];

        Assert.Contains("Interlocked.Increment(ref _quickSuggestionsRevision);", search);
        Assert.Contains("Publish(formChanged: false);", search);
        Assert.DoesNotContain("Publish();", search);
        Assert.Contains("NativeResourceVocabularySearchPhase.Error", search);
        Assert.Contains("state.IsQuickAddSearching", views);
        Assert.Contains("Views.IndeterminateProgress(text(\"Common_Loading\"))", views);
        Assert.Contains("NativeResourceVocabularySearchPhase.Searching", coordinator);

        var refresh = coordinator[coordinator.IndexOf(
                "internal void RefreshAddWords(SourceUiStyle style)", StringComparison.Ordinal)
            ..coordinator.IndexOf("private SwiftView[] AddWordsRows(", StringComparison.Ordinal)];
        Assert.Contains("if (revision == _renderedAddWordsRevision)", refresh);
        Assert.Contains("list.RemoveAt(0);", refresh);
        Assert.Contains("list.Add(row);", refresh);
        Assert.DoesNotContain("_addWordsSearch", refresh);
        Assert.Contains("_app.RefreshResourceAddWords(_style);", scene);
        Assert.DoesNotContain("Views.QuickAddSuggestions(", scene);
        Assert.DoesNotContain("HandleQuickAddKey", coordinator);
    }

    [Fact]
    public void UnsavedLanguageQuickAdd_PreservesMountedFormAndFocusAfterStaging()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var views = Source("Features/LearningResources/LearningResourceView.cs");
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var mutation = coordinator[coordinator.IndexOf(
                "private async Task MutateVocabularyAsync(", StringComparison.Ordinal)
            ..coordinator.IndexOf("private async Task ImportVocabularyTextAsync(",
                StringComparison.Ordinal)];

        // Captain 2026-11-13: native Resource form. The staged word joins the mounted
        // vocabulary Section in place (no rebuilt form, so focus and scroll stay).
        Assert.Contains("RegisterVocabularySection: section =>", coordinator);
        Assert.Contains("LearningResourceVocabularyView.AddVocabulary(sections, state,", views);
        Assert.Contains("inputs.RegisterVocabularySection(section);", vocabulary);
        // A word added from the add sheet (saved, or staged while the language is unsaved)
        // joins the Section in place: no first redraw, then the new rows only.
        Assert.Contains("var fromSheet = clearQuery && _addWordsOpen;", mutation);
        Assert.Contains("if (applyInPlace is null && !fromSheet)", mutation);
        Assert.Contains("appliedInPlace = fromSheet && TryAppendNewWordRows();", mutation);
        Assert.Contains("Publish(formChanged: !appliedInPlace);", mutation);
        Assert.Contains("!_knownWordIds.Contains(word.Id)", coordinator);
        Assert.Contains("section.Insert(insertAt++, LearningResourceVocabularyView.EditVocabularyRow(word,",
            coordinator);
        Assert.Contains("\"native.resources.form.vocabulary.empty\")", coordinator);
        Assert.Contains("search.Text = string.Empty;", mutation);
    }

    [Fact]
    public void FileImporterCancellationReadFailureAndOversizeStayDistinct()
    {
        var scene = Source("App/ApplicationScene.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        // Captain 2026-11-13: native Resource form. The file chooser wraps the import sheet,
        // which wraps the form; Choose File in the sheet opens it once the sheet has gone.
        Assert.Contains("Views.FileImporter(_importSheet,", scene);
        Assert.Contains("_chooseFileAfterImportSheet = true;", coordinator);
        Assert.Contains("_app.OnResourceFileImported,", scene);
        Assert.Contains("_app.OnResourceFileImportError,", scene);
        Assert.Contains("_app.OnFileImporterDismissed);", scene);
        Assert.Contains("FileImporterChanged?.Invoke(false);", coordinator);
        Assert.Contains("code == \"tooLarge\"", coordinator);
        Assert.Contains("NativeResources_FileTooLarge", coordinator);
        Assert.Contains("NativeResources_FileReadError", coordinator);
        Assert.Contains("_presenter.ImportVocabularyFromFileAsync(", coordinator);
        Assert.DoesNotContain("ex.Message", coordinator);
    }

    [Fact]
    public void DeleteRequiresSeparateRequestCancelAndOwnerCheckedConfirmation()
    {
        var scene = Source("App/ApplicationScene.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        // Captain 2026-11-13: native Resource form. The system confirmation dialog asks; its
        // close drops the request one main-loop turn later, after Delete has read it.
        Assert.Contains("Views.ConfirmationDialog(_fileImporter,", scene);
        Assert.Contains("ResourceEdit_DeleteMessage", scene);
        Assert.Contains("_app.ResourceDeleteDialogClosed,", scene);
        Assert.Contains(".WithButtonRole(SwiftButtonRole.Destructive)", scene);
        Assert.Contains("_app.CancelResourceDelete", scene);
        Assert.Contains("_app.ConfirmResourceDelete", scene);
        Assert.DoesNotContain("Views.ResourceDeleteConfirmation(_fileImporter,", scene);
        var closed = coordinator[coordinator.IndexOf(
                "internal void DeleteDialogClosed()", StringComparison.Ordinal)..];
        Assert.Contains("DispatchQueue.MainQueue.DispatchAsync(", closed[..600]);
        Assert.Contains("NativeResourceDetailPhase.Deleting", closed[..600]);
        Assert.Contains("if (_presenter.RequestDelete())", coordinator);
        Assert.Contains("if (_presenter.RequestDelete())\n            Publish(formChanged: false);",
            coordinator);
        Assert.Contains("_presenter.CancelDelete();", coordinator);
        Assert.Contains("_presenter.CancelDelete();\n        Publish(formChanged: false);",
            coordinator);
        Assert.Contains("!_presenter.Detail.ShowDeleteConfirmation", coordinator);
        Assert.Contains("_presenter.ConfirmDeleteAsync(_lifetime.Token)",
            coordinator);
        Assert.Contains("ResourceEdit_DeletedSuccess", coordinator);
        Assert.Contains("ResourceEdit_DeleteFailed", form);
    }

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string StringsPath => Path.GetFullPath(Path.Combine(
        HeadPath, "../SentenceStudio.Shared/Resources/Strings"));

    private static string Source(string relative) => File.ReadAllText(
        Path.Combine(HeadPath, relative));

    private static string NativeSource(string name) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(HeadPath,
            "../SentenceStudio.Native", name)));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
