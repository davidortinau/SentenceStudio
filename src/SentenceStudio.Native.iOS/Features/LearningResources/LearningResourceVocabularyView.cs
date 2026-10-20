using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal static class LearningResourceVocabularyView
{
    internal const string EditVocabularyRowsId =
        "native.resources.form.vocabulary.rows";

    internal static void AddVocabulary(
        List<SwiftView> fields, LearningResourceViewState state, bool creating,
        bool busy, LearningResourceActions actions, LearningResourceInputs inputs,
        SourceUiStyle style,
        Func<string, string> text)
    {
        fields.Add(style.Text(Views.Text(creating
                ? text("ResourceAdd_VocabularySection")
                : string.Format(CultureInfo.CurrentCulture,
                    text("ResourceEdit_VocabularySection"), state.Vocabulary.Count)),
                SourceTextRole.Title3, sourceHeading: true)
            .WithAccessibilityIdentifier("native.resources.form.vocabulary"));
        if (!creating && !state.IsSmartResource
            && !string.IsNullOrWhiteSpace(state.Transcript))
        {
            var generate = style.SecondaryButton(text(state.IsGenerating
                        ? "ResourceEdit_Generating"
                        : "ResourceEdit_GenerateFromTranscript"), actions.Generate)
                .WithAccessibilityIdentifier("native.resources.edit.generate");
            fields.Add(state.IsGenerating
                ? Views.HStack(
                        Views.IndeterminateProgress(
                                text("ResourceEdit_Generating"))
                            .WithAccessibilityIdentifier(
                                "native.resources.edit.generate.progress"),
                        generate)
                    .WithAccessibilityIdentifier("native.resources.edit.generate.busy")
                : generate);
        }
        if (!creating && !state.IsSmartResource)
        {
            fields.Add(LearningResourceQuickAddView.QuickAdd(
                state, busy, actions, inputs, style, text));
            fields.Add(style.SecondaryButton(
                    text("ResourceEdit_BulkImportToggle"), inputs.ToggleBulk)
                .WithAccessibilityIdentifier("native.resources.edit.bulk.toggle"));
        }
        if (creating || !state.IsSmartResource && inputs.BulkExpanded)
        {
            var pasteLabel = text(creating
                ? "ResourceAdd_PasteVocabLabel" : "ResourceEdit_ImportVocabLabel");
            fields.Add(LearningResourceFieldsView.Area(style, pasteLabel, inputs.PasteText,
                "native.resources.form.vocabulary.paste",
                inputs.PasteTextChanged, readOnly: false,
                rows: creating ? 6 : 4,
                placeholder: text(creating
                    ? "ResourceAdd_VocabPlaceholder" : "ResourceEdit_VocabPlaceholder")));
            var delimiter = style.Text(Views.Picker(
                    text("ResourceAdd_CommaDelimiter"),
                    [
                        text(creating ? "ResourceAdd_CommaDelimiter" : "ResourceEdit_Comma"),
                        text(creating ? "ResourceAdd_TabDelimiter" : "ResourceEdit_Tab"),
                    ], inputs.Delimiter == "tab" ? 1 : 0,
                    index => inputs.DelimiterChanged(index == 1 ? "tab" : "comma")),
                    SourceTextRole.Input)
                .WithPickerStyle(SwiftPickerStyle.Segmented)
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.delimiter");
            var import = style.SecondaryButton(text(creating
                    ? "ResourceAdd_ImportVocabulary" : "ResourceEdit_Import"),
                actions.ImportText)
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.import");
            import.IsEnabled = !busy && !string.IsNullOrWhiteSpace(inputs.PasteText);
            inputs.RegisterImport(import);
            var importRow = Views.HStack(delimiter, import)
                .WithAdaptiveStack(368)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.import.row");
            importRow.Spacing = SourceUiLayout.ItemGap;
            fields.Add(importRow);
            fields.Add(style.Text(Views.Text(text(creating
                ? "ResourceAdd_OrImportFile" : "ResourceEdit_OrImportFile")),
                SourceTextRole.Body2, SourceTextTone.Secondary));
            fields.Add(style.SecondaryButton(text(creating
                        ? "ResourceAdd_ChooseFile" : "ResourceEdit_ChooseFile"),
                    inputs.ChooseFile)
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.file"));
            if (state.FileStatus is { Length: > 0 } fileStatus)
                fields.Add(style.Text(Views.Text(fileStatus),
                        SourceTextRole.Caption1, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(
                        "native.resources.form.vocabulary.file.status"));
            if (state.IsImporting)
                fields.Add(Views.HStack(
                        Views.IndeterminateProgress(text(creating
                                ? "ResourceAdd_Importing" : "ResourceEdit_Importing"))
                            .WithAccessibilityIdentifier(
                                "native.resources.form.vocabulary.import.progress"),
                        style.Text(Views.Text(text(creating
                                ? "ResourceAdd_Importing" : "ResourceEdit_Importing")),
                            SourceTextRole.Caption1, SourceTextTone.Secondary))
                    .WithAccessibilityIdentifier(
                        "native.resources.form.vocabulary.file.loading"));
        }
        if (state.Vocabulary.Count == 0 && !creating)
            fields.Add(style.Text(Views.Text(text("ResourceEdit_NoVocabYet")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.empty"));
        else
            AddVocabularyRows(fields, state, creating, busy, actions, inputs,
                style, text);
    }

    private static void AddVocabularyRows(
        List<SwiftView> fields, LearningResourceViewState state,
        bool creating, bool busy, LearningResourceActions actions,
        LearningResourceInputs inputs, SourceUiStyle style, Func<string, string> text)
    {
        if (creating && state.Vocabulary.Count > 0)
        {
            var table = CreateStagedTable(state.Vocabulary, busy, actions, style, text);
            inputs.RegisterStagedGrid(table);
            fields.Add(table);
            var count = style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text("ResourceAdd_VocabWordsAdded"), state.Vocabulary.Count)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(
                    "native.resources.form.vocabulary.staged.count");
            inputs.RegisterStagedCount(count);
            fields.Add(count);
            return;
        }

        var rows = CreateEditVocabularyRows();
        foreach (var word in state.Vocabulary)
            AppendEditVocabularyRow(rows, word, state.IsSmartResource, busy,
                actions.RemoveWord, style, text);
        fields.Add(rows);
    }

    internal static SwiftView CreateEditVocabularyRows()
    {
        var rows = Views.VStack()
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(EditVocabularyRowsId);
        rows.Spacing = 0;
        return rows;
    }

    internal static void AppendEditVocabularyRow(
        SwiftView rows, LearningResourceWord word, bool isSmartResource, bool busy,
        Action<string> removeWord, SourceUiStyle style, Func<string, string> text)
    {
        if (rows.Kind != "vstack"
            || rows.AccessibilityIdentifier != EditVocabularyRowsId)
            throw new ArgumentException("Expected the Resource Edit vocabulary rows.", nameof(rows));
        if (rows.Children.Count > 0)
            rows.Add(Views.Divider()
                .WithBackgroundColor(style.Appearance.BorderSecondary.Light,
                    style.Appearance.BorderSecondary.Dark));
        rows.Add(EditVocabularyRow(word, isSmartResource, busy, removeWord,
                style, text)
            .WithPadding(SourceUiLayout.ItemGap, 0, SourceUiLayout.ItemGap, 0));
    }

    internal static SwiftView EditVocabularyRow(
        LearningResourceWord word, bool isSmartResource, bool busy,
        Action<string> removeWord, SourceUiStyle style, Func<string, string> text)
    {
        var id = word.Id;
        var target = style.Text(Views.Text(word.TargetTerm),
                SourceTextRole.Body1Bold, strongElement: true)
            .WithAccessibilityIdentifier(
                $"native.resources.form.vocabulary.target.{id}");
        var native = style.Text(Views.Text(string.IsNullOrEmpty(word.NativeTerm)
                ? string.Empty : "  " + word.NativeTerm),
                SourceTextRole.Body1, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier(
                $"native.resources.form.vocabulary.native.{id}");
        var terms = Views.InlineText(target, native)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(
                $"native.resources.form.vocabulary.terms.{id}");
        var cells = new List<SwiftView> { terms };
        if (!isSmartResource && !busy)
            cells.Add(RemoveWordButton(id, creating: false, busy, removeWord,
                style, text));
        var row = Views.HStack(cells.ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(
                $"native.resources.form.vocabulary.word.{id}");
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    private static SwiftView CreateStagedTable(
        IReadOnlyList<LearningResourceWord> vocabulary, bool busy,
        LearningResourceActions actions, SourceUiStyle style, Func<string, string> text)
    {
        SwiftView Header(string key, string id) =>
            style.Text(Views.Text(text(key)), SourceTextRole.Body2Strong)
                .WithAccessibilityIdentifier(
                    $"native.resources.add.table.header.{id}");

        var header = Views.HStack(
                Header("Import_TargetLanguageTerm", "target"),
                Header("Import_ColNative", "native"),
                Header("Import_ColType", "type"),
                Header("VocabWordEdit_Tags", "tags"),
                Views.Text(string.Empty)
                    .WithAccessibilityIdentifier(
                        "native.resources.add.table.header.remove"))
            .WithAccessibilityIdentifier("native.resources.add.table.header");

        var rows = vocabulary.Select(word =>
        {
            var id = word.Id;
            var target = style.Text(Views.Text(word.TargetTerm),
                    SourceTextRole.DefaultBody)
                .WithAccessibilityIdentifier(
                    $"native.resources.form.vocabulary.target.{id}");
            var native = style.Text(Views.Text(word.NativeTerm ?? string.Empty),
                    SourceTextRole.DefaultBody)
                .WithAccessibilityIdentifier(
                    $"native.resources.form.vocabulary.native.{id}");
            var kind = style.Input(Views.Picker(string.Empty,
                    [
                        text("Vocabulary_FilterTypeWord"),
                        text("Vocabulary_FilterTypePhrase"),
                        text("Vocabulary_FilterTypeSentence"),
                    ],
                    word.LexicalUnitType switch
                    {
                        LexicalUnitType.Phrase => 1,
                        LexicalUnitType.Sentence => 2,
                        _ => 0
                    }, selected => actions.SetWordType(id, selected switch
                    {
                        1 => LexicalUnitType.Phrase,
                        2 => LexicalUnitType.Sentence,
                        _ => LexicalUnitType.Word
                    }))
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithAccessibilityLabel(text("Import_ColType")))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(
                    $"native.resources.form.vocabulary.kind.{id}");
            var tags = style.Text(Views.Text(
                    string.IsNullOrWhiteSpace(word.Tags) ? "—" : word.Tags),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(
                    $"native.resources.form.vocabulary.tags.{id}");
            var remove = RemoveWordButton(id, creating: true, busy,
                actions.RemoveWord, style, text);
            return Views.HStack(target, native, kind, tags, remove)
                .WithAccessibilityIdentifier(
                    $"native.resources.form.vocabulary.word.{id}");
        }).ToArray();

        var grid = Views.HorizontalGrid([0, 0, 120, 0, 0], header, rows);
        grid.Spacing = SourceUiLayout.ItemGap;
        return grid.WithAccessibilityIdentifier("native.resources.add.table");
    }

    private static SwiftView RemoveWordButton(
        string id, bool creating, bool busy, Action<string> removeWord,
        SourceUiStyle style, Func<string, string> text)
    {
        var remove = Views.Button(Views.Image("xmark"),
                () => removeWord(id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithForegroundColor(
                creating ? style.Appearance.Danger.Light
                    : style.Appearance.SecondaryText.Light,
                creating ? style.Appearance.Danger.Dark
                    : style.Appearance.SecondaryText.Dark)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(text("ResourceEdit_RemoveWord"))
            .WithAccessibilityIdentifier(
                $"native.resources.form.vocabulary.remove.{id}");
        if (creating)
            remove.WithButtonAppearance(null, null,
                style.Appearance.Danger.Light,
                style.Appearance.Danger.Dark,
                style.Appearance.BootstrapSmallButtonRadius);
        remove.IsEnabled = !busy;
        return remove;
    }

}
