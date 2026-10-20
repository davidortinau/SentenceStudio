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
        Assert.Equal(2, scene.Split("Views.Tab(", StringSplitOptions.None).Length - 1);
        Assert.Contains("Views.Tab(localize(\"Dashboard_Title\"), \"house\",", scene);
        Assert.Contains("Views.Tab(localize(\"Resources_Title\"), \"books.vertical\",", scene);

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

    [Fact]
    public void BulkVocabularyShowsVisibleDelimiterNextToImportAndIconOnlyRemove()
    {
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var start = form.IndexOf("var delimiter = style.Text(Views.Picker(",
            StringComparison.Ordinal);
        var end = form.IndexOf("fields.Add(importRow);", start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var bulk = form[start..end];
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Segmented)", bulk);
        Assert.Contains("inputs.DelimiterChanged(index == 1 ? \"tab\" : \"comma\")",
            bulk);
        Assert.Contains("var importRow = Views.HStack(delimiter, import)", bulk);
        Assert.Contains("inputs.RegisterImport(import);", bulk);
        Assert.DoesNotContain("fields.Add(delimiter);", bulk);

        var rows = form[form.IndexOf("private static void AddVocabularyRows(",
                StringComparison.Ordinal)..];
        Assert.Contains("Views.Button(Views.Image(\"xmark\"),", rows);
        Assert.Contains("creating ? style.Appearance.Danger.Light", rows);
        Assert.Contains("style.Appearance.SecondaryText.Light", rows);
        Assert.Contains(".WithAccessibilityLabel(text(\"ResourceEdit_RemoveWord\"))",
            rows);
        Assert.DoesNotContain("style.SecondaryButton(\n"
            + "                        text(\"ResourceEdit_RemoveWord\")", rows);
    }

    [Fact]
    public void ResourceAddStagedWords_UseFiveAlignedHorizontallyScrollableColumns()
    {
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var start = form.IndexOf("private static void AddVocabularyRows(",
            StringComparison.Ordinal);
        var end = form.Length;
        Assert.True(start >= 0 && end > start);
        var vocabulary = form[start..end];

        Assert.Contains("if (creating && state.Vocabulary.Count > 0)", vocabulary);
        Assert.Contains("var table = CreateStagedTable(state.Vocabulary, busy, actions, style, text);",
            vocabulary);
        Assert.Contains("inputs.RegisterStagedGrid(table);", vocabulary);
        Assert.Contains("Views.HorizontalGrid([0, 0, 120, 0, 0], header, rows);",
            vocabulary);
        var headerStart = vocabulary.IndexOf("var header = Views.HStack(",
            StringComparison.Ordinal);
        var headerEnd = vocabulary.IndexOf("native.resources.add.table.header",
            headerStart, StringComparison.Ordinal);
        Assert.True(headerStart >= 0 && headerEnd > headerStart);
        var header = vocabulary[headerStart..headerEnd];
        var prior = -1;
        foreach (var key in new[]
        {
            "Import_TargetLanguageTerm", "Import_ColNative", "Import_ColType",
            "VocabWordEdit_Tags"
        })
        {
            var index = header.IndexOf($"Header(\"{key}\"", StringComparison.Ordinal);
            Assert.True(index > prior, $"{key} must follow the previous header");
            prior = index;
        }
        Assert.Contains("Views.Text(string.Empty)", header);
        Assert.Contains("Views.HStack(target, native, kind, tags, remove)",
            vocabulary);
        var pickerStart = vocabulary.IndexOf("var kind = style.Input(Views.Picker(",
            StringComparison.Ordinal);
        var pickerEnd = vocabulary.IndexOf("var tags =", pickerStart,
            StringComparison.Ordinal);
        Assert.True(pickerStart >= 0 && pickerEnd > pickerStart);
        var picker = vocabulary[pickerStart..pickerEnd];
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Menu)", picker);
        Assert.Contains(".WithFlexibleWidth()", picker);
        Assert.Contains(".WithAccessibilityLabel(text(\"Import_ColType\"))", picker);
        Assert.Contains("remove.IsEnabled = !busy;", vocabulary);
        Assert.Contains("actions.SetWordType(id,", vocabulary);
        Assert.Contains("actions.RemoveWord, style, text", vocabulary);
        Assert.Contains("() => removeWord(id)", vocabulary);
        Assert.Contains("string.IsNullOrWhiteSpace(word.Tags)", vocabulary);
        Assert.DoesNotContain("Views.TextField(word.TargetTerm", vocabulary);
        Assert.DoesNotContain("Views.TextField(word.NativeTerm", vocabulary);

        var edit = vocabulary[..vocabulary.IndexOf(
            "private static SwiftView CreateStagedTable(", StringComparison.Ordinal)];
        Assert.Contains("var terms = Views.InlineText(target, native)", edit);
        Assert.Contains("var row = Views.HStack(cells.ToArray())", edit);
        Assert.Contains("rows.Add(Views.Divider()", edit);
        Assert.Contains(".WithBackgroundColor(style.Appearance.BorderSecondary.Light,", edit);
        Assert.Contains("if (rows.Children.Count > 0)", edit);
        Assert.Contains("fields.Add(rows);", edit);
        Assert.Contains("string.IsNullOrEmpty(word.NativeTerm)", edit);
        Assert.Contains("SourceTextRole.Body1Bold, strongElement: true", edit);
        Assert.Contains("SourceTextRole.Body1, SourceTextTone.Secondary", edit);
        Assert.Contains("if (!isSmartResource && !busy)", edit);
        Assert.DoesNotContain(".WithAdaptiveStack(420)", edit);
        Assert.DoesNotContain(".WithAdaptiveStack(690)", edit);
        Assert.DoesNotContain("Views.HorizontalGrid(", edit);
        Assert.DoesNotContain("VocabWordEdit_Tags", edit);
    }

    [Fact]
    public void StagedTypeAndRemove_PreserveMountedTableUntilItsLastRow()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var form = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var type = coordinator[coordinator.IndexOf("SetWordType: (id, kind) =>",
                StringComparison.Ordinal)
            ..coordinator.IndexOf("ImportText: () =>", StringComparison.Ordinal)];
        Assert.Contains("_presenter.SetStagedLexicalUnitType(id, kind)", type);
        Assert.Contains("Publish(formChanged: false);", type);
        Assert.DoesNotContain("Publish();", type);

        var remove = coordinator[coordinator.IndexOf(
                "private void RemoveVocabulary(string id)", StringComparison.Ordinal)
            ..coordinator.IndexOf("private async Task MutateVocabularyAsync(",
                StringComparison.Ordinal)];
        Assert.Contains("if (_creating && _stagedGrid is { } grid", remove);
        Assert.Contains("for (var index = 1;", remove);
        Assert.Contains("grid.RemoveAt(index);", remove);
        Assert.Contains("detached.Dispose();", remove);
        Assert.True(remove.IndexOf("grid.RemoveAt(index);", StringComparison.Ordinal)
            < remove.IndexOf("detached.Dispose();", StringComparison.Ordinal));
        Assert.Contains("_stagedCount.Text = string.Format(", remove);
        Assert.Contains("Publish(formChanged: false);", remove);
        Assert.Contains("Publish();", remove);
        Assert.Contains("inputs.RegisterStagedGrid(table);", form);
        Assert.Contains("inputs.RegisterStagedCount(count);", form);
    }

    [Fact]
    public void ResourcesUseLazySwiftUIGridAndContiguousListGroupWithWholeRowActivation()
    {
        var resources = Source("Features/LearningResources/LearningResourcesView.cs");
        var row = Source("Features/LearningResources/LearningResourceRowView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var presenter = NativeSource("NativeResourcePresenter.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("SourceUiLayout.ResourceColumns(style.ViewportWidth)", resources);
        Assert.Contains("Views.LazyVGrid(1, rows.ToArray())", resources);
        Assert.Contains("if (rows.Count != 0)\n                rows.Add(Views.Divider());",
            resources);
        Assert.Contains("style.BorderedCard(group, radius: style.Appearance.BootstrapRadius)",
            resources);
        Assert.Contains("items.Add(ResourceListGroup(resources, text, actions.Open, style))",
            resources);
        Assert.DoesNotContain("Views.List(", resources);
        Assert.Contains("Views.Button(label, () => open(resource.Id))", row);
        Assert.Contains("SourceUiLayout.ShowResourceListMetadata(style.ViewportWidth)",
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
        Assert.Contains("_presenter.SetViewMode(mode)", coordinator);
        Assert.Contains("private const string ViewModeKey = \"resources-view-mode\"", presenter);
        Assert.Contains("Views.TextField(", resources);
        Assert.Contains("Search: Search,", coordinator);
        Assert.Contains("_resourcesStack = Views.NavigationStack(_resourcesSlot,", scene);
        Assert.DoesNotContain("Views.Searchable(_resourcesSlot,", scene);
        Assert.Contains("OnBrowserAppeared", coordinator);
        Assert.Contains("if (state.Phase == NativeResourceListPhase.Loading)",
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
        var list = row[row.IndexOf("if (list)\n        {\n            SwiftView[] contents",
                StringComparison.Ordinal)
            ..row.IndexOf("\n        else\n        {\n            label = Views.HStack",
                StringComparison.Ordinal)];

        Assert.Contains("SourceUiLayout.ShowResourceListMetadata(style.ViewportWidth)",
            list);
        Assert.Contains("style.Text(Views.Text(media), SourceTextRole.Small,", list);
        Assert.Contains(": [icon, name, time];", list);
        Assert.Contains("label = Views.HStack(contents);", list);
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
        Assert.Contains("editor.IsEnabled = !readOnly;", fields);
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
        Assert.Contains("var editor = style.Input(input)", fields);
        Assert.Contains("if (!readOnly && placeholder is not null)", fields);
        Assert.DoesNotContain("native.resources.form.vocabulary.example", vocabulary);

        var edit = form[form.IndexOf("if (!creating)\n        {\n            fields.Add(CardSection(style, \"native.resources.form.transcript.card\", [",
                StringComparison.Ordinal)
            ..form.IndexOf("var vocabulary = new List<SwiftView>();",
                StringComparison.Ordinal)];
        Assert.Contains("ResourceEdit_Transcript", edit);
        Assert.Contains("ResourceEdit_Translation", edit);
        Assert.DoesNotContain("placeholder:", edit);
        Assert.Contains("state.IsSmartResource, heading: true, rows: 6", edit);
    }

    [Fact]
    public void FullResourceCreateEditAndSmartReadOnlyUseBoundSwiftUICards()
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
        Assert.Contains("style.Page(Views.Scroll(content)", form);
        Assert.Contains("return style.Card(card);", form);
        Assert.DoesNotContain("Views.Form(", form);
        Assert.Contains("Views.Picker(", fields);
        Assert.Contains("state.IsSmartResource", form);
        Assert.Contains("ResourceEdit_AutoUpdated", form);
        Assert.Contains("_presenter.BeginCreate()", coordinator);
        Assert.Contains("_presenter.BeginEdit()", coordinator);
        Assert.Contains("_presenter.SaveAsync(_lifetime.Token)", coordinator);
        Assert.Contains("_formStatus = _text(\"ResourceEdit_SavedSuccess\")", coordinator);
        Assert.Contains("_presenter.CancelEdit();", coordinator);
        Assert.Contains("_resourcesStack.Push(\"resource-form\",", scene);
        Assert.Contains("_conflictAlert.WithTabBarHidden()", scene);
    }

    [Fact]
    public void CardBackedResourceEditorRetainsLiveInputsFocusAndQuickAddMutations()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        var fields = Source("Features/LearningResources/LearningResourceFieldsView.cs");
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        var quickAdd = Source("Features/LearningResources/LearningResourceQuickAddView.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("Views.TextField(placeholder ?? label,", fields);
        Assert.Contains("value ?? string.Empty, changed))", fields);
        Assert.Contains("Views.TextEditor(value ?? string.Empty,", fields);
        Assert.Contains("readOnly ? null : changed)\n            .WithTextEditorScrollBackgroundHidden();",
            fields);
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Menu)", fields);
        Assert.Contains("input.OnKeyPress(key => key switch", quickAdd);
        Assert.Contains(".OnFocusChanged(focused => inputs.QuickFocusChanged(input, focused))",
            quickAdd);
        Assert.Contains("inputs.RegisterQuickSearch(input);", quickAdd);
        Assert.Contains("inputs.RegisterImport(import);", vocabulary);
        Assert.Contains("inputs.RegisterSave(save);", form);

        Assert.Contains("inputs.RegisterVocabularySection(vocabularyCard);", form);
        Assert.Contains("section.Children[0].AccessibilityIdentifier", coordinator);
        Assert.Contains("section.Add(rows);", coordinator);
        Assert.Contains("LearningResourceVocabularyView.AppendEditVocabularyRow(rows,",
            coordinator);
        Assert.Contains("Views.FileImporter(_formSlot,", scene);
        Assert.Contains("Views.ResourceDeleteConfirmation(_fileImporter,", scene);
        Assert.Contains("Views.Alert(_deleteConfirmation,", scene);
        Assert.Contains("Views.QuickAddSuggestions(", scene);
        Assert.Contains("Find(content, fieldId) is not { Kind: \"textField\" }", scene);
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
        var scene = Source("App/ApplicationScene.cs");
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
        Assert.Contains("_resourcesStack.CanNavigateBack = !_app.NavigationBlocked",
            scene);
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
        Assert.Contains("if (creating || !state.IsSmartResource && inputs.BulkExpanded)",
            form);
        Assert.Contains("state.QuickAddResults", quickAdd);
        Assert.Contains("native.resources.form.vocabulary.remove.{id}", form);
        Assert.Contains("text(\"ResourceEdit_QuickAddPlaceholder\"),",
            quickAdd);
        Assert.Contains("input.OnKeyPress(key => key switch", quickAdd);
        Assert.Contains("SwiftTextKey.Escape =>", quickAdd);
        Assert.Contains("SwiftTextKey.Enter =>", quickAdd);
        Assert.Contains("SwiftTextKey.ArrowUp =>", quickAdd);
        Assert.Contains("SwiftTextKey.ArrowDown =>", quickAdd);
        Assert.DoesNotContain("input.OnEscape(", quickAdd);
        Assert.Contains("option.WithAccessibilitySelected();", quickAdd);
        Assert.Contains("createOption.WithAccessibilitySelected();", quickAdd);
        Assert.Contains("registerOption(option);", quickAdd);
        Assert.Contains("registerOption(createOption);", quickAdd);
        Assert.DoesNotContain("parts.Add(dropdown);", quickAdd);
        Assert.Contains("_quickOptionNodes.Count == count", coordinator);
        Assert.Contains("option.WithAccessibilitySelected(selected);", coordinator);
        Assert.Contains("UpdateQuickOptionSelection(appearance);", coordinator);
        Assert.Contains("RenderQuickAddSuggestions(SourceUiStyle style)", coordinator);
        Assert.Contains("internal bool IsQuickAddOpen", coordinator);
        Assert.Contains("internal bool HasQuickAddField", coordinator);
        Assert.Contains("internal void DismissQuickAddSuggestions()", coordinator);
        Assert.Contains("Views.IndeterminateProgress(text(\"Common_Loading\"))",
            quickAdd);
        Assert.Contains("inputs.RegisterQuickSearch(input)", quickAdd);
        Assert.Contains("native.resources.edit.quick.listbox", quickAdd);
        Assert.True(form.IndexOf("fields.Add(LearningResourceQuickAddView.QuickAdd(",
                StringComparison.Ordinal)
            < form.IndexOf("AddVocabularyRows(fields,", StringComparison.Ordinal));
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
        Assert.Contains("style.PrimaryButton(text(\"ResourceEdit_QuickAddLabel\"),",
            quickAdd);
        Assert.Contains("actions.CreateWord)", quickAdd);
        Assert.Contains("actions.ImportText)", form);
        Assert.Contains("PasteTextChanged: value =>", coordinator);
        Assert.Contains("UpdateImportButtons();", coordinator);
        Assert.Contains("inputs.RegisterImport(import);", form);
        Assert.DoesNotContain("actions.CreateWord(inputs.QuickQuery", quickAdd);
        Assert.DoesNotContain("actions.ImportText(inputs.PasteText", form);
    }

    [Fact]
    public void QuickAddQueryPublishesOverlayChangesWithoutRemountingFocusedForm()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var views = Source("Features/LearningResources/LearningResourceQuickAddView.cs");
        var search = coordinator[coordinator.IndexOf(
                "private async Task SearchVocabularyAsync(", StringComparison.Ordinal)
            ..coordinator.IndexOf("private Task AttachVocabularyAsync(",
                StringComparison.Ordinal)];

        Assert.Contains("internal long QuickAddSuggestionsVersion", coordinator);
        Assert.Contains("Interlocked.Increment(ref _quickSuggestionsRevision);", search);
        Assert.Contains("Publish(formChanged: false);", search);
        Assert.DoesNotContain("Publish();", search);
        Assert.Contains("NativeResourceVocabularySearchPhase.Error", search);
        Assert.Contains("state.IsQuickAddSearching", views);
        Assert.Contains("Views.IndeterminateProgress(text(\"Common_Loading\"))", views);
        Assert.Contains("NativeResourceVocabularySearchPhase.Searching", coordinator);
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

        Assert.Contains("RegisterVocabularySection: section =>", coordinator);
        Assert.Contains("inputs.RegisterVocabularySection(vocabularyCard);", views);
        Assert.Contains("TryAppendStagedQuickAdd(", mutation);
        Assert.Contains("Publish(formChanged: !appendedStagedWord);", mutation);
        Assert.Contains("section.Add(rows);", coordinator);
        Assert.Contains("field.Text = string.Empty;", coordinator);
        Assert.Contains("LearningResourceVocabularyView.CreateEditVocabularyRows()",
            coordinator);
        Assert.Contains("LearningResourceVocabularyView.AppendEditVocabularyRow(rows,",
            coordinator);
        Assert.Contains("rows.Add(EditVocabularyRow(", vocabulary);
        Assert.Contains("if (rows.Children.Count > 0)", vocabulary);
    }

    [Fact]
    public void FileImporterCancellationReadFailureAndOversizeStayDistinct()
    {
        var scene = Source("App/ApplicationScene.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        Assert.Contains("Views.FileImporter(_formSlot,", scene);
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
        Assert.Contains("Views.ResourceDeleteConfirmation(_fileImporter,", scene);
        Assert.Contains("ResourceEdit_DeleteMessage", scene);
        Assert.Contains("_app.CancelResourceDelete", scene);
        Assert.Contains("_app.ConfirmResourceDelete", scene);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceQuickAddEscape_ReplacesOnlyUnmodifiedInheritedEscape(
        bool emptyBase)
    {
        Shortcut[]? inherited = emptyBase ? [] :
        [
            new("Return", 0, "textFieldReturn:"),
            new("Escape", 0, "systemEscape:"),
            new("Escape", 1, "modifiedEscape:"),
            new("Return", 0, "duplicateReturn:"),
        ];
        var escape = new Shortcut("Escape", 0, "resourceQuickEscape:");
        var merged = NativeResourceQuickKeyCommands.Merge(
            inherited, [escape, escape],
            command => (command.Input, command.Modifiers));
        if (emptyBase)
            Assert.Collection(merged, command => Assert.Same(escape, command));
        else
            Assert.Collection(merged,
                command => Assert.Same(inherited![0], command),
                command => Assert.Same(inherited![2], command),
                command => Assert.Same(escape, command));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceQuickAddKeyCommands_NilOrEmptyBaseRetainsEachLocalActionOnce(
        bool emptyBase)
    {
        var local = QuickCommands();
        Shortcut[]? inherited = emptyBase ? [] : null;
        var merged = NativeResourceQuickKeyCommands.Merge(
            inherited, local, command => (command.Input, command.Modifiers));
        Assert.Equal(3, merged.Length);
        for (var index = 0; index < local.Length; index++)
            Assert.Same(local[index], merged[index]);
    }

    [Fact]
    public void ResourceQuickAddKeyCommands_PreservesUnrelatedBaseActionsAndOwnsSearchKeys()
    {
        Shortcut[] inherited =
        [
            new("Return", 0, "textFieldReturn:"),
            new("N", 1, "nativeNew:"),
            new("Down", 0, "oldDown:"),
            new("Down", 1, "modifiedDown:"),
            new("Return", 0, "duplicateReturn:"),
        ];
        var local = QuickCommands();
        Shortcut[] withDuplicateLocal = [.. local, new("Down", 0, "duplicateDown:")];
        var merged = NativeResourceQuickKeyCommands.Merge(
            inherited, withDuplicateLocal,
            command => (command.Input, command.Modifiers));
        Assert.Collection(merged,
            command => Assert.Same(inherited[0], command),
            command => Assert.Same(inherited[1], command),
            command => Assert.Same(inherited[3], command),
            command => Assert.Same(local[0], command),
            command => Assert.Same(local[1], command),
            command => Assert.Same(local[2], command));
        Assert.DoesNotContain(merged, command =>
            command.Selector is "oldDown:" or "duplicateReturn:" or "duplicateDown:");
    }

    [Fact]
    public void ResourceQuickAddKeyCommands_InactiveDoesNotAlterBaseShortcuts()
    {
        Shortcut[] inherited =
        [
            new("Escape", 0, "systemEscape:"),
            new("Return", 0, "textFieldReturn:"),
        ];
        var result = NativeResourceQuickKeyCommands.Merge(
            inherited, null, command => (command.Input, command.Modifiers));
        Assert.Same(inherited, result);
    }

    private static Shortcut[] QuickCommands() =>
    [
        new("Up", 0, "resourceQuickUp:"),
        new("Down", 0, "resourceQuickDown:"),
        new("Escape", 0, "resourceQuickEscape:"),
    ];

    private sealed record Shortcut(string Input, int Modifiers, string Selector);

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
