using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Shared.Models;
using Color = AndroidX.Compose.Color;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The word editor's Reference Sentences card (source ReferenceSentencesSection, restored at the
/// Captain's request 2026-11-10, as on iOS): Generate beside the heading, the add row, the AI
/// suggestions to keep or discard, then the owner's sentences, core first, each with its actions
/// menu. The state and every action are the shared VocabularyBrowser's; each change saves at
/// once, apart from the word's own Save.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private static readonly SpeechRegister[] ExampleRegisters = Enum.GetValues<SpeechRegister>();

    private ComposableNode VocabularyExamplesCard(VocabularyBrowser vocabulary,
        VocabularyBrowserEdit state, AndroidVisualPalette palette)
    {
        var examples = state.Examples ?? VocabularyExamplesState.Empty;
        // The word's Save, Delete or a handoff holds the whole editor; a sentence write holds
        // the fields (it may clear them when it ends).
        var locked = state.Saving || state.Deleting || state.HandingOff;
        var busy = locked || examples.Writing;

        var generateLabel = Localize(examples.Generating ? "RefSentences_Generating" : "RefSentences_Generate");
        var generate = new OutlinedButton(() => RunMoreOnUi(vocabulary.GenerateExamplesAsync),
            enabled: !examples.Generating && !locked)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag("native.vocabulary.edit.examples.generate")
                .Semantics(semantics => semantics.ContentDescription(generateLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        var generateContent = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically);
        if (examples.Generating)
            generateContent.Add(new CircularProgressIndicator { Modifier = Modifier.Size(16) });
        generateContent.Add(new Text(generateLabel));
        generate.Add(generateContent);

        var items = new List<ComposableNode>
        {
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Box { Modifier.Weight(1f), WordSectionHeading("RefSentences_Title", palette) },
                generate,
            },
            Caption(Localize("RefSentences_Help"), palette, "native.vocabulary.edit.examples.help"),
            ExampleField(examples.NewTarget, "RefSentences_AddTargetPlaceholder",
                "native.vocabulary.edit.examples.new.target",
                value => vocabulary.ChangeNewExample(VocabularyExampleField.Target, value), !busy),
            ExampleField(examples.NewNative, "RefSentences_AddNativePlaceholder",
                "native.vocabulary.edit.examples.new.native",
                value => vocabulary.ChangeNewExample(VocabularyExampleField.Native, value), !busy),
            // Bottom-aligned: the dropdown's label sits above its field, level with nothing.
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.Bottom)
            {
                Modifier.FillMaxWidth(),
                new Box
                {
                    Modifier.Weight(1f),
                    ExampleRegisterDropdown("vocabulary.example.new.register",
                        "native.vocabulary.edit.examples.new.register", examples.NewRegister,
                        vocabulary.SetNewExampleRegister, !busy, palette),
                },
                ExampleButton(Localize("RefSentences_Add"), "native.vocabulary.edit.examples.add",
                    () => RunMoreOnUi(vocabulary.AddExampleAsync), !locked && examples.CanAdd),
            },
        };
        if (examples.Message is { } message)
            items.Add(StatusAlert(message, palette, error: examples.MessageIsError,
                automationId: "native.vocabulary.edit.examples.message",
                success: !examples.MessageIsError));
        if (examples.Suggestions.Count > 0)
            items.Add(ExampleSuggestionTray(vocabulary, examples, busy, palette));
        if (examples.Sentences.Count == 0 && examples.Suggestions.Count == 0)
            items.Add(Body(Localize("RefSentences_Empty"), palette, secondary: true,
                modifier: Modifier.TestTag("native.vocabulary.edit.examples.empty")));
        foreach (var sentence in examples.Sentences)
            items.Add(examples.EditingId == sentence.Id
                ? EditedExample(vocabulary, sentence, examples, locked, busy, palette)
                : ExampleSentence(vocabulary, sentence, busy, palette));
        return Card(palette, "native.vocabulary.edit.examples", items.ToArray());
    }

    /// <summary>Source suggestion tray: each one editable, with its register and level, Keep and Discard.</summary>
    private ComposableNode ExampleSuggestionTray(VocabularyBrowser vocabulary,
        VocabularyExamplesState examples, bool busy, AndroidVisualPalette palette)
    {
        var tray = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(12),
            Body(Localize("RefSentences_Suggestions"), palette,
                modifier: Modifier.TestTag("native.vocabulary.edit.examples.suggestions.title")),
        };
        foreach (var suggestion in examples.Suggestions)
        {
            var key = suggestion.Key;
            var id = $"native.vocabulary.edit.examples.suggestion.{key}";
            var card = new Column(Arrangement.SpacedBy(8))
            {
                Modifier.FillMaxWidth().Padding(12).TestTag(id),
                ExampleField(suggestion.TargetSentence, "RefSentences_AddTargetPlaceholder", $"{id}.target",
                    value => vocabulary.ChangeSuggestion(key, VocabularyExampleField.Target, value), !busy),
                ExampleField(suggestion.NativeSentence, "RefSentences_AddNativePlaceholder", $"{id}.native",
                    value => vocabulary.ChangeSuggestion(key, VocabularyExampleField.Native, value), !busy),
            };
            if (ExampleBadges(null, suggestion.Register, suggestion.DifficultyLevel, false,
                    $"{id}.badges", palette) is { } badges)
                card.Add(badges);
            card.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                ExampleButton(Localize("RefSentences_Keep"), $"{id}.keep",
                    () => RunMoreOnUi(() => vocabulary.KeepSuggestionAsync(key)),
                    !busy && VocabularyExamplesState.CanKeep(examples.Writing, suggestion)),
                ExampleButton(Localize("RefSentences_Discard"), $"{id}.discard",
                    () => vocabulary.DiscardSuggestion(key), !busy, primary: false),
            });
            tray.Add(ExampleSurface(card, palette, palette.Border));
        }
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .Border(1, palette.Primary, AndroidVisualTheme.ControlRadius)
                .TestTag("native.vocabulary.edit.examples.suggestions"),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Elevated,
        };
        surface.Add(tray);
        return surface;
    }

    /// <summary>A saved sentence: its text and translation, its badges, and its actions menu.</summary>
    private ComposableNode ExampleSentence(VocabularyBrowser vocabulary, NativeVocabularyExample sentence,
        bool busy, AndroidVisualPalette palette)
    {
        var id = $"native.vocabulary.edit.example.{sentence.Id}";
        var sentenceId = sentence.Id;
        var text = new Column(Arrangement.SpacedBy(2))
        {
            Modifier.Weight(1f),
            Body(sentence.TargetSentence, palette, modifier: Modifier.TestTag($"{id}.target")),
        };
        if (!string.IsNullOrWhiteSpace(sentence.NativeSentence))
            text.Add(Body(sentence.NativeSentence, palette, secondary: true,
                modifier: Modifier.TestTag($"{id}.native")));

        var menuKey = $"vocabulary.example.{sentence.Id}";
        var actionsLabel = Localize("RefSentences_Actions");
        var button = new IconButton(() => ToggleMoreMenu(menuKey), enabled: !busy)
        {
            Modifier = Modifier.TestTag($"{id}.actions")
                .Semantics(semantics => semantics.ContentDescription(actionsLabel)),
        };
        button.Add(new Icon(Icons.Default.MoreVert, actionsLabel)
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var menu = PopupMenu(expanded: _moreMenu == menuKey, onDismissRequest: CloseMoreMenu);
        menu.Add(VocabularyMenuItem(Localize(sentence.IsCore ? "RefSentences_UnsetCore" : "RefSentences_SetCore"),
            $"{id}.core", !busy, () => RunMoreOnUi(() => vocabulary.ToggleExampleCoreAsync(sentenceId))));
        menu.Add(VocabularyMenuItem(Localize("RefSentences_Edit"), $"{id}.edit", !busy,
            () => vocabulary.StartEditExample(sentenceId)));
        menu.Add(VocabularyMenuItem(Localize(sentence.IsFlagged ? "RefSentences_Unflag" : "RefSentences_Flag"),
            $"{id}.flag", !busy, () => RunMoreOnUi(() => vocabulary.ToggleExampleFlagAsync(sentenceId))));
        menu.Add(new HorizontalDivider { Color = palette.Border });
        // Source: the Delete item is styled text-danger.
        menu.Add(VocabularyMenuItem(Localize("RefSentences_Delete"), $"{id}.delete", !busy,
            () => RunMoreOnUi(() => vocabulary.DeleteExampleAsync(sentenceId)),
            busy ? null : palette.Danger));

        var content = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(12).TestTag(id),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.Top)
            {
                Modifier.FillMaxWidth(),
                text,
                new Box { button, menu },
            },
        };
        if (ExampleBadges(sentence, sentence.Register, sentence.DifficultyLevel, sentence.IsFlagged,
                $"{id}.badges", palette) is { } badges)
            content.Add(badges);
        // Source: a flagged sentence has a warning border (its Flagged badge says so in words).
        return ExampleSurface(content, palette, sentence.IsFlagged ? palette.Warning : palette.Border);
    }

    /// <summary>Source inline edit: the sentence, its translation and register, Save and Cancel.</summary>
    private ComposableNode EditedExample(VocabularyBrowser vocabulary, NativeVocabularyExample sentence,
        VocabularyExamplesState examples, bool locked, bool busy, AndroidVisualPalette palette)
    {
        var id = $"native.vocabulary.edit.example.{sentence.Id}.editor";
        var content = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(12).TestTag(id),
            ExampleField(examples.EditTarget, "RefSentences_AddTargetPlaceholder", $"{id}.target",
                value => vocabulary.ChangeEditedExample(VocabularyExampleField.Target, value), !busy),
            ExampleField(examples.EditNative, "RefSentences_AddNativePlaceholder", $"{id}.native",
                value => vocabulary.ChangeEditedExample(VocabularyExampleField.Native, value), !busy),
            ExampleRegisterDropdown($"vocabulary.example.{sentence.Id}.register", $"{id}.register",
                examples.EditRegister, vocabulary.SetEditedExampleRegister, !busy, palette),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                ExampleButton(Localize("RefSentences_Save"), $"{id}.save",
                    () => RunMoreOnUi(vocabulary.SaveEditedExampleAsync), !locked && examples.CanSaveEdit),
                ExampleButton(Localize("RefSentences_Cancel"), $"{id}.cancel",
                    vocabulary.CancelEditExample, !busy, primary: false),
            },
        };
        return ExampleSurface(content, palette, palette.Primary);
    }

    /// <summary>
    /// Source badges: Core, where the sentence came from, its register, its level and Flagged,
    /// each in words (never colour alone), wrapping onto more lines when they do not fit. Null
    /// when there is nothing to say.
    /// </summary>
    private static ComposableNode? ExampleBadges(NativeVocabularyExample? sentence,
        SpeechRegister register, int? difficulty, bool flagged, string id, AndroidVisualPalette palette)
    {
        var badges = new List<ComposableNode>();
        if (sentence?.IsCore == true)
            badges.Add(ExampleBadge(Localize("RefSentences_Core"), $"{id}.core", palette, strong: true));
        if (sentence is not null && VocabularyExampleText.SourceKey(sentence.Source) is { } sourceKey)
            badges.Add(ExampleBadge(Localize(sourceKey), $"{id}.source", palette));
        if (register != SpeechRegister.Unspecified)
            badges.Add(ExampleBadge(Localize(VocabularyExampleText.RegisterKey(register)),
                $"{id}.register", palette));
        if (difficulty is { } level)
            badges.Add(ExampleBadge(string.Format(CultureInfo.CurrentCulture,
                Localize("RefSentences_Level"), level), $"{id}.level", palette));
        if (flagged)
            badges.Add(ExampleBadge(Localize("RefSentences_Flagged"), $"{id}.flagged", palette));
        if (badges.Count == 0)
            return null;
        var row = new FlowRow { Modifier.FillMaxWidth().TestTag(id) };
        foreach (var badge in badges)
            row.Add(badge);
        return row;
    }

    private static ComposableNode ExampleBadge(string label, string tag, AndroidVisualPalette palette,
        bool strong = false)
    {
        var surface = new Surface
        {
            Modifier = Modifier.Padding(end: 6, bottom: 4).TestTag(tag)
                .Border(1, strong ? palette.Primary : palette.Border, 12),
            Shape = Shape.RoundedCorners(12),
            Color = strong ? palette.Primary : palette.Card,
        };
        surface.Add(new Text(label)
        {
            Modifier = Modifier.Padding(horizontal: 10, vertical: 4),
            Color = strong ? palette.OnPrimary : palette.Text,
            FontSize = new Sp(AndroidVisualTheme.CaptionSize),
        });
        return surface;
    }

    private static ComposableNode ExampleSurface(ComposableNode content, AndroidVisualPalette palette,
        Color border)
    {
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().Border(1, border, AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Card,
        };
        surface.Add(content);
        return surface;
    }

    /// <summary>A sentence or translation field; typing updates the shared draft and redraws at once (IME composition stays).</summary>
    private ComposableNode ExampleField(string value, string labelKey, string tag,
        Action<string> changed, bool enabled)
    {
        var label = Localize(labelKey);
        return new OutlinedTextField(value, text =>
        {
            changed(text);
            RenderMoreNow();
        }, enabled: enabled, singleLine: false)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Label = new Text(label),
        };
    }

    private ComposableNode ExampleRegisterDropdown(string key, string tag, SpeechRegister selected,
        Action<SpeechRegister> changed, bool enabled, AndroidVisualPalette palette) =>
        MoreDropdown(key, tag, Localize("RefSentences_Register"),
            ExampleRegisters.Select(register => (register.ToString(),
                Localize(VocabularyExampleText.RegisterKey(register)))).ToArray(),
            selected.ToString(), value =>
            {
                if (Enum.TryParse<SpeechRegister>(value, out var register))
                    changed(register);
            }, enabled, palette);

    /// <summary>A content-width button (source btn-sm), filled or outlined.</summary>
    private static ComposableNode ExampleButton(string label, string tag, Action command, bool enabled,
        bool primary = true)
    {
        var modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight).TestTag(tag)
            .Semantics(semantics => semantics.ContentDescription(label));
        if (!primary)
        {
            var outlined = new OutlinedButton(onClick: command, enabled: enabled)
            {
                Modifier = modifier,
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            outlined.Add(new Text(label));
            return outlined;
        }
        var button = new ComposeButton(onClick: command, enabled: enabled)
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Text(label));
        return button;
    }
}
