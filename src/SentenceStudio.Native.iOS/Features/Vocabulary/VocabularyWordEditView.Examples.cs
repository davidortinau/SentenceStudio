using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

/// <summary>
/// The word editor's Reference Sentences Section (source ReferenceSentencesSection, restored at
/// the Captain's request 2026-11-10; native Form rows 2026-11-13): Generate, the add rows, the
/// AI suggestions to keep or discard, then the owner's sentences, core first, each with its
/// actions menu. Every
/// change saves at once, as the source does; the word's own Save is not involved.
/// </summary>
internal static partial class VocabularyWordEditView
{
    /// <summary>The buttons whose availability follows typing (VocabularyCoordinator updates them in place).</summary>
    internal const string AddExampleButton = "add";
    internal const string SaveEditedExampleButton = "edit.save";
    internal static string KeepSuggestionButton(string key) => $"keep.{key}";

    private static readonly SpeechRegister[] Registers = Enum.GetValues<SpeechRegister>();

    internal static SwiftView ExamplesCard(
        VocabularyBrowserEdit state, SourceUiStyle style, Func<string, string> text,
        VocabularyExampleActions actions)
    {
        var examples = state.Examples ?? VocabularyExamplesState.Empty;
        // The word's Save, Delete or a handoff holds the whole editor.
        var locked = state.Saving || state.Deleting || state.HandingOff;
        // Captain 2026-11-13 (the native form pattern): the card is a Form Section of rows.
        // Generate is an accent action row; the add fields are field rows; each sentence is a
        // row with its actions menu.
        var generateLabel = examples.Generating
            ? Views.HStack(Views.IndeterminateProgress(text("RefSentences_Generating")),
                Views.Text(text("RefSentences_Generating")))
            : Views.Label(text("RefSentences_Generate"), "sparkles");
        if (examples.Generating)
            generateLabel.Spacing = SourceUiLayout.ItemGap;
        // One accessible name: the spinner and the text would otherwise both be read.
        var generate = NativeForm.Action(style, Views.Button(generateLabel, actions.Generate)
            .WithAccessibilityLabel(text(examples.Generating
                ? "RefSentences_Generating" : "RefSentences_Generate"))
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.generate"));
        generate.IsEnabled = !examples.Generating && !locked;

        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("RefSentences_Help")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.examples.help"),
            generate
        };
        if (examples.Message is { } message)
            rows.Add(NativeForm.Notice(style, message,
                    "native.vocabulary.edit.examples.message", examples.MessageIsError)
                .WithAccessibilityAnnouncement(message));
        // A write in progress makes the fields read-only: it may clear them when it ends.
        rows.AddRange(AddExampleRows(examples, locked || examples.Writing, style, text, actions));
        if (examples.Suggestions.Count > 0)
            rows.AddRange(SuggestionRows(examples, locked || examples.Writing, style, text,
                actions));
        if (examples.Sentences.Count == 0 && examples.Suggestions.Count == 0)
            rows.Add(style.Text(Views.Text(text("RefSentences_Empty")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.examples.empty"));
        foreach (var sentence in examples.Sentences)
        {
            if (examples.EditingId == sentence.Id)
                rows.AddRange(EditedSentenceRows(sentence, examples,
                    locked || examples.Writing, style, text, actions));
            else
                rows.Add(Sentence(sentence, examples.Writing || locked, style, text, actions));
        }
        return Views.Section(text("RefSentences_Title"), rows.ToArray());
    }

    /// <summary>The add rows: the sentence, its translation, its register, and Add.</summary>
    private static IEnumerable<SwiftView> AddExampleRows(
        VocabularyExamplesState examples, bool locked, SourceUiStyle style,
        Func<string, string> text, VocabularyExampleActions actions)
    {
        yield return SentenceField(text("RefSentences_AddTargetPlaceholder"), examples.NewTarget,
            value => actions.ChangeNew(VocabularyExampleField.Target, value),
            "native.vocabulary.edit.examples.new.target", !locked);
        yield return SentenceField(text("RefSentences_AddNativePlaceholder"), examples.NewNative,
            value => actions.ChangeNew(VocabularyExampleField.Native, value),
            "native.vocabulary.edit.examples.new.native", !locked);
        yield return RegisterPicker(examples.NewRegister, actions.SetNewRegister,
            "native.vocabulary.edit.examples.new.register", !locked, style, text);
        var add = NativeForm.Action(style, Views.Button(
                Views.Label(text("RefSentences_Add"), "plus.circle"), actions.Add)
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.add"));
        add.IsEnabled = !locked && examples.CanAdd;
        actions.RegisterButton?.Invoke(AddExampleButton, add);
        yield return add;
    }

    /// <summary>
    /// Source suggestion tray: a heading row, then each suggestion's fields (editable), its
    /// register and difficulty, and Keep and Discard side by side (bordered buttons, each its
    /// own target in the row).
    /// </summary>
    private static IEnumerable<SwiftView> SuggestionRows(
        VocabularyExamplesState examples, bool locked, SourceUiStyle style,
        Func<string, string> text, VocabularyExampleActions actions)
    {
        yield return style.Text(Views.Label(text("RefSentences_Suggestions"), "sparkles"),
                SourceTextRole.Body2, SourceTextTone.Secondary, strongElement: true)
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.suggestions.title");
        foreach (var suggestion in examples.Suggestions)
        {
            var id = $"native.vocabulary.edit.examples.suggestion.{suggestion.Key}";
            yield return SentenceField(text("RefSentences_AddTargetPlaceholder"),
                suggestion.TargetSentence,
                value => actions.ChangeSuggestion(suggestion.Key, VocabularyExampleField.Target, value),
                $"{id}.target", !locked);
            yield return SentenceField(text("RefSentences_AddNativePlaceholder"),
                suggestion.NativeSentence,
                value => actions.ChangeSuggestion(suggestion.Key, VocabularyExampleField.Native, value),
                $"{id}.native", !locked);
            var keep = NativeForm.Prominent(style, text("RefSentences_Keep"),
                () => actions.Keep(suggestion.Key), $"{id}.keep");
            keep.IsEnabled = !locked
                && VocabularyExamplesState.CanKeep(examples.Writing, suggestion);
            actions.RegisterButton?.Invoke(KeepSuggestionButton(suggestion.Key), keep);
            var discard = NativeForm.Bordered(style, text("RefSentences_Discard"),
                () => actions.Discard(suggestion.Key), $"{id}.discard");
            discard.IsEnabled = !locked && !examples.Writing;
            // The badges take the free width, so the buttons sit at the trailing edge.
            var badges = Views.VStack(Badges(null, suggestion.Register,
                    suggestion.DifficultyLevel, false, $"{id}.badges", style, text))
                .WithFlexibleWidth();
            var buttons = Views.HStack(badges, keep, discard)
                .WithAccessibilityIdentifier(id);
            buttons.Spacing = SourceUiLayout.ItemGap;
            yield return buttons;
        }
    }

    /// <summary>A saved sentence row: its text and translation, its badges, and its actions menu.</summary>
    private static SwiftView Sentence(
        NativeVocabularyExample sentence, bool busy, SourceUiStyle style,
        Func<string, string> text, VocabularyExampleActions actions)
    {
        var id = $"native.vocabulary.edit.example.{sentence.Id}";
        var lines = new List<SwiftView> {
            style.Text(Views.Text(sentence.TargetSentence), SourceTextRole.Body1)
                .WithAccessibilityIdentifier($"{id}.target")
        };
        if (!string.IsNullOrWhiteSpace(sentence.NativeSentence))
            lines.Add(style.Text(Views.Text(sentence.NativeSentence),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"{id}.native"));
        lines.AddRange(Badges(sentence, sentence.Register, sentence.DifficultyLevel,
            sentence.IsFlagged, $"{id}.badges", style, text));
        var textColumn = Views.VStack(lines.ToArray()).WithFlexibleWidth();
        textColumn.Spacing = 4;

        var core = Views.Button(text(sentence.IsCore ? "RefSentences_UnsetCore"
                : "RefSentences_SetCore"), () => actions.ToggleCore(sentence.Id))
            .WithAccessibilityIdentifier($"{id}.core");
        var edit = Views.Button(text("RefSentences_Edit"), () => actions.StartEdit(sentence.Id))
            .WithAccessibilityIdentifier($"{id}.edit");
        var flag = Views.Button(text(sentence.IsFlagged ? "RefSentences_Unflag"
                : "RefSentences_Flag"), () => actions.ToggleFlag(sentence.Id))
            .WithAccessibilityIdentifier($"{id}.flag");
        var delete = Views.Button(text("RefSentences_Delete"), () => actions.Delete(sentence.Id))
            .WithButtonRole(SwiftButtonRole.Destructive)
            .WithAccessibilityIdentifier($"{id}.delete");
        // A Menu's label fills its width (bridge); fixed at a 44-point target, the text keeps the rest.
        var menu = NativeForm.Action(style, Views.Menu(Views.Image("ellipsis.circle"), core, edit,
                    flag, Views.Divider(), delete)
                .WithFixedWidth(44)
                .WithAccessibilityLabel(text("RefSentences_Actions"))
                .WithAccessibilityIdentifier($"{id}.actions"));
        menu.IsEnabled = !busy;
        var row = Views.HStack(textColumn, menu).WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    /// <summary>Source inline edit: the sentence, its translation and register, Save and Cancel.</summary>
    private static IEnumerable<SwiftView> EditedSentenceRows(
        NativeVocabularyExample sentence, VocabularyExamplesState examples, bool locked,
        SourceUiStyle style, Func<string, string> text, VocabularyExampleActions actions)
    {
        var id = $"native.vocabulary.edit.example.{sentence.Id}.editor";
        yield return SentenceField(text("RefSentences_AddTargetPlaceholder"), examples.EditTarget,
            value => actions.ChangeEdit(VocabularyExampleField.Target, value), $"{id}.target",
            !locked);
        yield return SentenceField(text("RefSentences_AddNativePlaceholder"), examples.EditNative,
            value => actions.ChangeEdit(VocabularyExampleField.Native, value), $"{id}.native",
            !locked);
        yield return RegisterPicker(examples.EditRegister, actions.SetEditRegister,
            $"{id}.register", !locked, style, text);
        var save = NativeForm.Prominent(style, text("RefSentences_Save"), actions.SaveEdit,
            $"{id}.save");
        save.IsEnabled = !locked && examples.CanSaveEdit;
        actions.RegisterButton?.Invoke(SaveEditedExampleButton, save);
        var cancel = NativeForm.Bordered(style, text("RefSentences_Cancel"), actions.CancelEdit,
            $"{id}.cancel");
        cancel.IsEnabled = !locked && !examples.Writing;
        // An empty flexible leading column puts the buttons at the trailing edge.
        var buttons = Views.HStack(Views.VStack().WithFlexibleWidth(), cancel, save)
            .WithAccessibilityIdentifier(id);
        buttons.Spacing = SourceUiLayout.ItemGap;
        yield return buttons;
    }

    /// <summary>
    /// Source badges: Core, where the sentence came from, its register, its difficulty and
    /// Flagged, each in words (never colour alone), in one row that scrolls sideways when it
    /// does not fit. None when there is nothing to say.
    /// </summary>
    private static SwiftView[] Badges(
        NativeVocabularyExample? sentence, SpeechRegister register, int? difficulty,
        bool flagged, string id, SourceUiStyle style, Func<string, string> text)
    {
        var appearance = style.Appearance;
        var badges = new List<SwiftView>();
        if (sentence?.IsCore == true)
            badges.Add(Badge(text("RefSentences_Core"), $"{id}.core", strong: true));
        if (sentence is not null && VocabularyExampleText.SourceKey(sentence.Source) is { } sourceKey)
            badges.Add(Badge(text(sourceKey), $"{id}.source", strong: false));
        if (register != SpeechRegister.Unspecified)
            badges.Add(Badge(RegisterLabel(register, text), $"{id}.register", strong: false));
        if (difficulty is { } level)
            badges.Add(Badge(string.Format(CultureInfo.CurrentCulture,
                text("RefSentences_Level"), level), $"{id}.level", strong: false));
        if (flagged)
            badges.Add(Badge(text("RefSentences_Flagged"), $"{id}.flagged", strong: false));
        if (badges.Count == 0)
            return [];
        var row = Views.HStack(badges.ToArray());
        row.Spacing = 6;
        return [Views.HorizontalGrid(new double[badges.Count], row)
            .WithAccessibilityIdentifier(id)];

        SwiftView Badge(string label, string badgeId, bool strong) =>
            style.Text(Views.Text(label), SourceTextRole.BadgeSmall)
                .WithPadding(4, 8, 4, 8)
                .WithRoundedSurface(
                    strong ? appearance.Primary.Light : appearance.SecondarySurface.Light,
                    strong ? appearance.Primary.Dark : appearance.SecondarySurface.Dark, 10,
                    strong ? appearance.Primary.Light : appearance.Border.Light,
                    strong ? appearance.Primary.Dark : appearance.Border.Dark, 1)
                .WithForegroundColor(
                    strong ? appearance.OnPrimary.Light : appearance.Text.Light,
                    strong ? appearance.OnPrimary.Dark : appearance.Text.Dark)
                .WithAccessibilityIdentifier(badgeId);
    }

    /// <summary>A sentence field row: the prompt names it, the row is the field.</summary>
    private static SwiftView SentenceField(
        string placeholder, string value, Action<string> changed, string id, bool enabled)
    {
        var field = Views.TextField(placeholder, value, changed)
            .WithAccessibilityLabel(placeholder)
            .WithAccessibilityIdentifier(id);
        field.IsEnabled = enabled;
        return field;
    }

    /// <summary>The register as a system menu Picker row: the label beside the current value.</summary>
    private static SwiftView RegisterPicker(
        SpeechRegister value, Action<SpeechRegister> changed, string id, bool enabled,
        SourceUiStyle style, Func<string, string> text)
    {
        var picker = NativeForm.Choice(style, text("RefSentences_Register"),
            Registers.Select(register => RegisterLabel(register, text)).ToArray(),
            Math.Max(0, Array.IndexOf(Registers, value)), id,
            index => changed(Registers[Math.Clamp(index, 0, Registers.Length - 1)]));
        picker.IsEnabled = enabled;
        return picker;
    }

    /// <summary>Source RegisterLabel.</summary>
    internal static string RegisterLabel(SpeechRegister register, Func<string, string> text) =>
        text(VocabularyExampleText.RegisterKey(register));
}
