using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

/// <summary>
/// The Resource form's vocabulary (Captain 2026-11-13): each word is a native Form row, swiped
/// left to remove; an existing resource imports in bulk from a sheet, a new one in place.
/// </summary>
internal static class LearningResourceVocabularyView
{
    // The vocabulary and staged-words Sections have no automation ID (SwiftUI would give it to
    // every row); the coordinator keeps the registered Section itself.

    internal static void AddVocabulary(
        List<SwiftView> sections, LearningResourceViewState state, bool creating,
        bool busy, LearningResourceActions actions, LearningResourceInputs inputs,
        SourceUiStyle style, Func<string, string> text)
    {
        if (creating)
        {
            sections.Add(Views.Section(text("ResourceAdd_VocabularySection"),
                    ImportRows(state, creating: true, busy, actions, inputs, style, text,
                        includeImport: true).ToArray()));
            if (state.Vocabulary.Count > 0)
            {
                var staged = Views.Section(StagedHeader(state.Vocabulary.Count, text),
                        state.Vocabulary.Select(word =>
                            StagedRow(word, busy, actions, style, text)).ToArray());
                inputs.RegisterStagedGrid(staged);
                sections.Add(staged);
            }
            return;
        }

        var rows = new List<SwiftView>();
        if (!state.IsSmartResource && !string.IsNullOrWhiteSpace(state.Transcript))
            rows.Add(state.IsGenerating
                ? Views.IndeterminateProgress(text("ResourceEdit_Generating"))
                    .WithAccessibilityIdentifier("native.resources.edit.generate.progress")
                : Action(style, Views.Button(Views.Label(
                        text("ResourceEdit_GenerateFromTranscript"), "sparkles"),
                        actions.Generate)
                    .WithAccessibilityIdentifier("native.resources.edit.generate")));
        if (!state.IsSmartResource)
        {
            rows.AddRange(LearningResourceQuickAddView.QuickAdd(
                state, busy, actions, inputs, style, text));
            rows.Add(Action(style, Views.Button(Views.Label(
                    text("ResourceEdit_BulkImportToggle"), "square.and.arrow.down"),
                    inputs.ToggleBulk)
                .WithAccessibilityIdentifier("native.resources.edit.bulk.toggle")));
            if (state.IsImporting)
                rows.Add(Views.IndeterminateProgress(text("ResourceEdit_Importing"))
                    .WithAccessibilityIdentifier(
                        "native.resources.form.vocabulary.import.progress"));
            if (state.FileStatus is { Length: > 0 } fileStatus)
                rows.Add(style.Text(Views.Text(fileStatus),
                        SourceTextRole.Caption1, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(
                        "native.resources.form.vocabulary.file.status"));
        }
        if (state.Vocabulary.Count == 0)
            rows.Add(EmptyRow(style, text));
        else
            rows.AddRange(state.Vocabulary.Select(word =>
                EditVocabularyRow(word, state.IsSmartResource, busy,
                    actions.RemoveWord, style, text)));
        var section = Views.Section(VocabularyHeader(state.Vocabulary.Count, text),
                rows.ToArray());
        inputs.RegisterVocabularySection(section);
        sections.Add(section);
    }

    internal static string VocabularyHeader(int count, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture,
            text("ResourceEdit_VocabularySection"), count);

    internal static string StagedHeader(int count, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture,
            text("ResourceAdd_VocabWordsAdded"), count);

    internal static SwiftView EmptyRow(SourceUiStyle style, Func<string, string> text) =>
        style.Text(Views.Text(text("ResourceEdit_NoVocabYet")),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier("native.resources.form.vocabulary.empty");

    /// <summary>
    /// The bulk import rows: the pasted words, their delimiter, Import (unless a toolbar holds
    /// it), and a file to import instead, with the file's status and progress.
    /// </summary>
    internal static List<SwiftView> ImportRows(
        LearningResourceViewState state, bool creating, bool busy,
        LearningResourceActions actions, LearningResourceInputs inputs,
        SourceUiStyle style, Func<string, string> text, bool includeImport)
    {
        var pasteLabel = text(creating
            ? "ResourceAdd_PasteVocabLabel" : "ResourceEdit_ImportVocabLabel");
        var rows = new List<SwiftView>
        {
            LearningResourceFieldsView.Editor(style, pasteLabel, inputs.PasteText,
                "native.resources.form.vocabulary.paste", inputs.PasteTextChanged,
                rows: creating ? 6 : 8, placeholder: text(creating
                    ? "ResourceAdd_VocabPlaceholder" : "ResourceEdit_VocabPlaceholder")),
            Views.Picker(text("ResourceAdd_CommaDelimiter"),
                    [
                        text(creating ? "ResourceAdd_CommaDelimiter" : "ResourceEdit_Comma"),
                        text(creating ? "ResourceAdd_TabDelimiter" : "ResourceEdit_Tab"),
                    ], inputs.Delimiter == "tab" ? 1 : 0,
                    index => inputs.DelimiterChanged(index == 1 ? "tab" : "comma"))
                .WithPickerStyle(SwiftPickerStyle.Segmented)
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.delimiter"),
        };
        if (includeImport)
        {
            var import = Views.Button(text(creating
                    ? "ResourceAdd_ImportVocabulary" : "ResourceEdit_Import"),
                actions.ImportText)
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.import");
            import.IsEnabled = !busy && !string.IsNullOrWhiteSpace(inputs.PasteText);
            inputs.RegisterImport(import);
            rows.Add(Action(style, import));
        }
        rows.Add(Action(style, Views.Button(Views.Label(text(creating
                    ? "ResourceAdd_ChooseFile" : "ResourceEdit_ChooseFile"), "doc"),
                inputs.ChooseFile)
            .WithAccessibilityIdentifier("native.resources.form.vocabulary.file")));
        if (creating && state.FileStatus is { Length: > 0 } fileStatus)
            rows.Add(style.Text(Views.Text(fileStatus),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.form.vocabulary.file.status"));
        if (creating && state.IsImporting)
            rows.Add(Views.IndeterminateProgress(text("ResourceAdd_Importing"))
                .WithAccessibilityIdentifier(
                    "native.resources.form.vocabulary.import.progress"));
        return rows;
    }

    /// <summary>
    /// An existing resource's word: the term and its meaning. Swipe left to remove it, except
    /// on a smart resource (read only) or while the form is busy.
    /// </summary>
    internal static SwiftView EditVocabularyRow(
        LearningResourceWord word, bool isSmartResource, bool busy,
        Action<string> removeWord, SourceUiStyle style, Func<string, string> text)
    {
        var id = word.Id;
        var content = Terms(word, style);
        if (isSmartResource || busy)
            return content.WithAccessibilityIdentifier(
                $"native.resources.form.vocabulary.word.{id}");
        return Views.SwipeRow(content, [RemoveAction(id, removeWord, style, text)])
            .WithAccessibilityIdentifier($"native.resources.form.vocabulary.word.{id}");
    }

    /// <summary>
    /// A new resource's staged word: the term, its meaning and tags, and its type (word,
    /// phrase or sentence) as a menu picker. Swipe left to remove it.
    /// </summary>
    internal static SwiftView StagedRow(
        LearningResourceWord word, bool busy, LearningResourceActions actions,
        SourceUiStyle style, Func<string, string> text)
    {
        var id = word.Id;
        var details = new List<SwiftView> { Terms(word, style) };
        if (!string.IsNullOrWhiteSpace(word.Tags))
            details.Add(style.Text(Views.Text(word.Tags),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"native.resources.form.vocabulary.tags.{id}"));
        var info = Views.VStack(details.ToArray()).WithFlexibleWidth();
        info.Spacing = 2;
        // Untitled: the row shows only the chosen type; its accessible name says what it is.
        var kind = Views.Picker(string.Empty,
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
            .WithAccessibilityLabel(text("Import_ColType"))
            .WithAccessibilityIdentifier($"native.resources.form.vocabulary.kind.{id}");
        var content = Views.HStack(info, kind).WithFlexibleWidth();
        if (busy)
            return content.WithAccessibilityIdentifier(
                $"native.resources.form.vocabulary.word.{id}");
        return Views.SwipeRow(content, [RemoveAction(id, actions.RemoveWord, style, text)])
            .WithAccessibilityIdentifier($"native.resources.form.vocabulary.word.{id}");
    }

    private static SwiftView Terms(LearningResourceWord word, SourceUiStyle style)
    {
        var id = word.Id;
        var target = style.Text(Views.Text(word.TargetTerm), SourceTextRole.Body1Bold,
                strongElement: true)
            .WithAccessibilityIdentifier($"native.resources.form.vocabulary.target.{id}");
        if (string.IsNullOrEmpty(word.NativeTerm))
            return Views.HStack(target)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"native.resources.form.vocabulary.terms.{id}");
        var native = style.Text(Views.Text(word.NativeTerm),
                SourceTextRole.Body1, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier($"native.resources.form.vocabulary.native.{id}");
        var terms = Views.HStack(target, native)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"native.resources.form.vocabulary.terms.{id}");
        terms.Spacing = SourceUiLayout.ItemGap;
        return terms;
    }

    /// <summary>A Form action row in the app's accent color, as a system Form draws its buttons.</summary>
    internal static SwiftView Action(SourceUiStyle style, SwiftView button) =>
        button.WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark);

    /// <summary>Remove: destructive (red), so the row leaves at once; no confirmation, as before.</summary>
    private static SwiftView RemoveAction(
        string id, Action<string> removeWord, SourceUiStyle style,
        Func<string, string> text) =>
        Views.Button(Views.Label(text("ResourceEdit_RemoveWord"), "trash"),
                () => removeWord(id))
            .WithButtonRole(SwiftButtonRole.Destructive)
            // The app's tint would otherwise color it; a removal is red.
            .WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
            .WithAccessibilityIdentifier($"native.resources.form.vocabulary.remove.{id}");
}
