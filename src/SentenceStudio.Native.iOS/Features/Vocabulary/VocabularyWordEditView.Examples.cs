using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

/// <summary>
/// The word editor's Reference Sentences card (source ReferenceSentencesSection, restored at the
/// Captain's request 2026-11-10): Generate in the header, the add row, the AI suggestions to
/// keep or discard, then the owner's sentences, core first, each with its actions menu. Every
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
        var generateLabel = examples.Generating
            ? Views.HStack(Views.IndeterminateProgress(text("RefSentences_Generating")),
                Views.Text(text("RefSentences_Generating")))
            : Views.HStack(Views.Image("sparkles"), Views.Text(text("RefSentences_Generate")));
        generateLabel.Spacing = 6;
        // One accessible name: the spinner and the text would otherwise both be read.
        var generate = style.OutlineButton(generateLabel, actions.Generate)
            .WithAccessibilityLabel(text(examples.Generating
                ? "RefSentences_Generating" : "RefSentences_Generate"))
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.generate");
        generate.IsEnabled = !examples.Generating && !locked;

        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("RefSentences_Help")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.examples.help"),
            // A write in progress makes the fields read-only: it may clear them when it ends.
            AddExampleRow(examples, locked || examples.Writing, style, text, actions)
        };
        if (examples.Message is { } message)
            rows.Add(style.Alert(style.Text(Views.Text(message), SourceTextRole.Body2),
                    success: !examples.MessageIsError, compact: true)
                .WithAccessibilityAnnouncement(message)
                .WithAccessibilityIdentifier("native.vocabulary.edit.examples.message"));
        if (examples.Suggestions.Count > 0)
            rows.Add(SuggestionTray(examples, locked || examples.Writing, style, text, actions));
        if (examples.Sentences.Count == 0 && examples.Suggestions.Count == 0)
            rows.Add(style.Text(Views.Text(text("RefSentences_Empty")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.vocabulary.edit.examples.empty"));
        rows.AddRange(examples.Sentences.Select(sentence => examples.EditingId == sentence.Id
            ? EditedSentence(sentence, examples, locked || examples.Writing, style, text, actions)
            : Sentence(sentence, examples.Writing || locked, style, text, actions)));
        return CardWithHeaderAction(style, "RefSentences_Title", text, generate, rows.ToArray());
    }

    private static SwiftView AddExampleRow(
        VocabularyExamplesState examples, bool locked, SourceUiStyle style,
        Func<string, string> text, VocabularyExampleActions actions)
    {
        var target = SentenceField(text("RefSentences_AddTargetPlaceholder"), examples.NewTarget,
            value => actions.ChangeNew(VocabularyExampleField.Target, value),
            "native.vocabulary.edit.examples.new.target", !locked, style);
        var native = SentenceField(text("RefSentences_AddNativePlaceholder"), examples.NewNative,
            value => actions.ChangeNew(VocabularyExampleField.Native, value),
            "native.vocabulary.edit.examples.new.native", !locked, style);
        var register = RegisterPicker(examples.NewRegister, actions.SetNewRegister,
            "native.vocabulary.edit.examples.new.register", !locked, style, text);
        var add = style.PrimaryButton(text("RefSentences_Add"), actions.Add)
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.add");
        SourceUiStyle.SetCustomButtonAvailability(add, style.Appearance,
            SourceButtonRole.Primary, !locked && examples.CanAdd);
        actions.RegisterButton?.Invoke(AddExampleButton, add);
        var controls = Views.HStack(register.WithFlexibleWidth(), add).WithFlexibleWidth();
        controls.Spacing = SourceUiLayout.ItemGap;
        var row = Views.VStack(target, native, controls).WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.new");
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    /// <summary>Source suggestion tray: each one editable, with its register and difficulty, Keep and Discard.</summary>
    private static SwiftView SuggestionTray(
        VocabularyExamplesState examples, bool locked, SourceUiStyle style,
        Func<string, string> text, VocabularyExampleActions actions)
    {
        var appearance = style.Appearance;
        var heading = Views.HStack(Views.Image("sparkles"),
                style.Text(Views.Text(text("RefSentences_Suggestions")), SourceTextRole.Body2,
                    strongElement: true))
            .WithForegroundColor(appearance.InfoAlertText.Light, appearance.InfoAlertText.Dark)
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.suggestions.title");
        heading.Spacing = 6;
        var items = new List<SwiftView> { heading };
        foreach (var suggestion in examples.Suggestions)
        {
            var id = $"native.vocabulary.edit.examples.suggestion.{suggestion.Key}";
            var target = SentenceField(text("RefSentences_AddTargetPlaceholder"),
                suggestion.TargetSentence,
                value => actions.ChangeSuggestion(suggestion.Key, VocabularyExampleField.Target, value),
                $"{id}.target", !locked, style);
            var native = SentenceField(text("RefSentences_AddNativePlaceholder"),
                suggestion.NativeSentence,
                value => actions.ChangeSuggestion(suggestion.Key, VocabularyExampleField.Native, value),
                $"{id}.native", !locked, style);
            var keep = style.PrimaryButton(text("RefSentences_Keep"),
                    () => actions.Keep(suggestion.Key))
                .WithAccessibilityIdentifier($"{id}.keep");
            SourceUiStyle.SetCustomButtonAvailability(keep, appearance, SourceButtonRole.Primary,
                !locked && VocabularyExamplesState.CanKeep(examples.Writing, suggestion));
            actions.RegisterButton?.Invoke(KeepSuggestionButton(suggestion.Key), keep);
            var discard = style.OutlineButton(Views.Text(text("RefSentences_Discard")),
                    () => actions.Discard(suggestion.Key))
                .WithAccessibilityIdentifier($"{id}.discard");
            discard.IsEnabled = !locked && !examples.Writing;
            var buttons = Views.HStack(keep, discard);
            buttons.Spacing = SourceUiLayout.ItemGap;
            var card = Views.VStack([target, native,
                    .. Badges(null, suggestion.Register, suggestion.DifficultyLevel, false,
                        $"{id}.badges", style, text),
                    buttons])
                .WithPadding(12)
                .WithAccessibilityIdentifier(id);
            card.Spacing = SourceUiLayout.ItemGap;
            items.Add(style.BorderedCard(card));
        }
        var tray = Views.VStack(items.ToArray())
            .WithPadding(12)
            .WithFlexibleWidth()
            .WithRoundedSurface(appearance.InfoAlertFill.Light, appearance.InfoAlertFill.Dark,
                appearance.BootstrapRadius, appearance.InfoAlertBorder.Light,
                appearance.InfoAlertBorder.Dark, 1)
            .WithAccessibilityIdentifier("native.vocabulary.edit.examples.suggestions");
        tray.Spacing = SourceUiLayout.ItemGap;
        return tray;
    }

    /// <summary>A saved sentence: its text and translation, its badges, and its actions menu.</summary>
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
        var textColumn = Views.VStack(lines.ToArray()).WithFlexibleWidth();
        textColumn.Spacing = 2;

        var core = Views.Button(text(sentence.IsCore ? "RefSentences_UnsetCore"
                : "RefSentences_SetCore"), () => actions.ToggleCore(sentence.Id))
            .WithAccessibilityIdentifier($"{id}.core");
        var edit = Views.Button(text("RefSentences_Edit"), () => actions.StartEdit(sentence.Id))
            .WithAccessibilityIdentifier($"{id}.edit");
        var flag = Views.Button(text(sentence.IsFlagged ? "RefSentences_Unflag"
                : "RefSentences_Flag"), () => actions.ToggleFlag(sentence.Id))
            .WithAccessibilityIdentifier($"{id}.flag");
        var delete = Views.Button(text("RefSentences_Delete"), () => actions.Delete(sentence.Id))
            .WithAccessibilityIdentifier($"{id}.delete");
        // A Menu's label fills its width (bridge); fixed at a 44-point target, the text keeps the rest.
        var menu = Views.Menu(Views.Image("ellipsis.circle"), core, edit, flag,
                Views.Divider(), delete)
            .WithFixedWidth(44)
            .WithAccessibilityLabel(text("RefSentences_Actions"))
            .WithAccessibilityIdentifier($"{id}.actions");
        menu.IsEnabled = !busy;
        var top = Views.HStack(textColumn, menu).WithFlexibleWidth();
        top.Spacing = SourceUiLayout.ItemGap;
        var content = Views.VStack([top,
                .. Badges(sentence, sentence.Register, sentence.DifficultyLevel,
                    sentence.IsFlagged, $"{id}.badges", style, text)])
            .WithPadding(12)
            .WithAccessibilityIdentifier(id);
        content.Spacing = SourceUiLayout.ItemGap;
        return style.BorderedCard(content);
    }

    /// <summary>Source inline edit: the sentence, its translation and register, Save and Cancel.</summary>
    private static SwiftView EditedSentence(
        NativeVocabularyExample sentence, VocabularyExamplesState examples, bool locked,
        SourceUiStyle style, Func<string, string> text, VocabularyExampleActions actions)
    {
        var id = $"native.vocabulary.edit.example.{sentence.Id}.editor";
        var target = SentenceField(text("RefSentences_AddTargetPlaceholder"), examples.EditTarget,
            value => actions.ChangeEdit(VocabularyExampleField.Target, value), $"{id}.target", !locked, style);
        var native = SentenceField(text("RefSentences_AddNativePlaceholder"), examples.EditNative,
            value => actions.ChangeEdit(VocabularyExampleField.Native, value), $"{id}.native", !locked, style);
        var register = RegisterPicker(examples.EditRegister, actions.SetEditRegister,
            $"{id}.register", !locked, style, text);
        var save = style.PrimaryButton(text("RefSentences_Save"), actions.SaveEdit)
            .WithAccessibilityIdentifier($"{id}.save");
        SourceUiStyle.SetCustomButtonAvailability(save, style.Appearance,
            SourceButtonRole.Primary, !locked && examples.CanSaveEdit);
        actions.RegisterButton?.Invoke(SaveEditedExampleButton, save);
        var cancel = style.OutlineButton(Views.Text(text("RefSentences_Cancel")),
                actions.CancelEdit)
            .WithAccessibilityIdentifier($"{id}.cancel");
        cancel.IsEnabled = !locked && !examples.Writing;
        var buttons = Views.HStack(register.WithFlexibleWidth(), save, cancel).WithFlexibleWidth();
        buttons.Spacing = SourceUiLayout.ItemGap;
        var content = Views.VStack(target, native, buttons)
            .WithPadding(12)
            .WithAccessibilityIdentifier(id);
        content.Spacing = SourceUiLayout.ItemGap;
        return style.BorderedCard(content);
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

    private static SwiftView SentenceField(
        string placeholder, string value, Action<string> changed, string id, bool enabled,
        SourceUiStyle style)
    {
        var field = style.TextInput(Views.TextField(placeholder, value, changed))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        field.IsEnabled = enabled;
        return field;
    }

    private static SwiftView RegisterPicker(
        SpeechRegister value, Action<SpeechRegister> changed, string id, bool enabled,
        SourceUiStyle style, Func<string, string> text)
    {
        var selected = Math.Max(0, Array.IndexOf(Registers, value));
        var picker = style.Input(Views.Picker(text("RefSentences_Register"),
                    Registers.Select(register => RegisterLabel(register, text)).ToArray(),
                    selected,
                    index => changed(Registers[Math.Clamp(index, 0, Registers.Length - 1)]))
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithFlexibleWidth())
            .WithAccessibilityIdentifier(id);
        picker.IsEnabled = enabled;
        return picker;
    }

    /// <summary>Source RegisterLabel.</summary>
    internal static string RegisterLabel(SpeechRegister register, Func<string, string> text) =>
        text(VocabularyExampleText.RegisterKey(register));
}
