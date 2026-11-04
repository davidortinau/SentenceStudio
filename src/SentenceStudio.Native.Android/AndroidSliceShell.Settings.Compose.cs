using System.Collections.Immutable;
using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Settings;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Settings Voice and Quiz card (source Settings.razor, iOS QuizSettingsView):
/// voice language and voice, prompt direction, autoplay, mnemonics, prompt
/// modalities and the approved 1-10 s auto-advance in 0.5 s steps (AF-Q14, AF-Q15),
/// saved through the shared owner-gated NativeQuizSettings.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ComposableNode RenderQuizSettingsSection(
        SettingsState state, QuizSettingsDraft quiz, AndroidVisualPalette palette)
    {
        var enabled = !Busy.Value;
        var rows = new List<ComposableNode>
        {
            Heading(Localize("Settings_VoiceQuizSection"), palette, 20,
                Modifier.Semantics(semantics => semantics.Heading())),
            MoreDropdown("settings.voice-language", "native.settings.quiz.voice-language",
                Localize("Settings_LanguageLabel"),
                NativeQuizSettings.Languages.Select(language =>
                    (language, Localize(LanguageNameKey(language)))).ToArray(),
                quiz.VoiceLanguage, SelectSettingsVoiceLanguage, enabled, palette),
        };
        if (state.VoiceLoading)
            rows.Add(Body(Localize("Common_Loading"), palette, secondary: true,
                modifier: Modifier.TestTag("native.settings.quiz.voices.loading")));
        else if (!quiz.VoiceOptions.IsDefaultOrEmpty || !string.IsNullOrWhiteSpace(quiz.VoiceId))
        {
            var voices = quiz.VoiceOptions.IsDefaultOrEmpty
                ? ImmutableArray.Create(new NativeQuizVoiceOption(
                    quiz.VoiceId, Localize("NativeSettings_CurrentVoice")))
                : quiz.VoiceOptions;
            rows.Add(MoreDropdown("settings.voice", "native.settings.quiz.voice",
                Localize("Settings_VoiceLabel"),
                voices.Select(option => (option.Id, option.Name)).ToArray(),
                quiz.VoiceId, voice =>
                {
                    CloseMoreMenu();
                    EditQuizSettings(draft => draft with { VoiceId = voice });
                }, enabled, palette));
        }
        if (state.VoiceErrorKey is { } voiceError)
            rows.Add(Caption(Localize(voiceError), palette, "native.settings.quiz.voices.error"));

        rows.Add(Strong(Localize("Settings_PromptLanguageLabel"), palette));
        rows.Add(MoreSegments("native.settings.quiz.direction",
            [
                ("TargetToNative", Localize("Settings_TargetLanguage")),
                ("NativeToTarget", Localize("Settings_NativeLanguage")),
                ("Mixed", Localize("Settings_Mixed")),
            ], quiz.Direction,
            value => EditQuizSettings(draft => draft with { Direction = value }), enabled));
        rows.Add(Caption(Localize("Settings_PromptLanguageHelp"), palette));
        rows.Add(SettingSwitch(Localize("Settings_AutoplayAudio"),
            "native.settings.quiz.autoplay", quiz.AutoplayAudio,
            value => EditQuizSettings(draft => draft with { AutoplayAudio = value }),
            enabled, palette));
        rows.Add(SettingSwitch(Localize("Settings_ShowMnemonics"),
            "native.settings.quiz.mnemonics", quiz.ShowMnemonics,
            value => EditQuizSettings(draft => draft with { ShowMnemonics = value }),
            enabled, palette));
        rows.Add(Strong(Localize("Settings_PromptModalitiesLabel"), palette));
        rows.Add(SettingSwitch(Localize("Settings_TextModality"),
            "native.settings.quiz.text", quiz.TextPrompt,
            value => EditQuizSettings(draft => draft with { TextPrompt = value }),
            enabled, palette));
        rows.Add(SettingSwitch(Localize("Settings_AudioModality"),
            "native.settings.quiz.audio", quiz.AudioPrompt,
            value => EditQuizSettings(draft => draft with { AudioPrompt = value }),
            enabled, palette));
        rows.Add(SettingSwitch(Localize("Settings_PhotoModality"),
            "native.settings.quiz.photo", quiz.PhotoPrompt,
            value => EditQuizSettings(draft => draft with { PhotoPrompt = value }),
            enabled, palette));
        rows.Add(Caption(Localize("Settings_ModalitiesHelp"), palette));

        var duration = DurationTitle(quiz.AutoAdvanceSeconds);
        rows.Add(Body(duration, palette,
            modifier: Modifier.TestTag("native.settings.quiz.duration.label")));
        rows.Add(new Slider((float)quiz.AutoAdvanceSeconds,
            raw => EditQuizSettings(draft => draft with
            {
                AutoAdvanceSeconds = QuizSettingsDraft.SnapDuration(raw)
            }),
            enabled: enabled,
            // NativeQuizTiming.ApprovedDurationsMs: 1 to 10 s in 0.5 s steps, 19 values.
            steps: NativeQuizTiming.ApprovedDurationsMs.Count - 2)
        {
            ValueRange = new FloatRange(1f, 10f),
            Modifier = Modifier.FillMaxWidth().TestTag("native.settings.quiz.duration")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(duration);
                    semantics.StateDescription(duration);
                }),
        });
        rows.Add(Action(Localize("Settings_SavePreferences"), "native.settings.quiz.save",
            SaveQuizSettings, enabled && !state.VoiceLoading));
        return Card(palette, "native.settings.quiz", rows.ToArray());
    }

    private static string DurationTitle(double seconds) =>
        string.Format(CultureInfo.CurrentCulture,
            Localize("Settings_AutoAdvanceDurationLabel"),
            seconds.ToString("0.#", CultureInfo.CurrentCulture));

    private static string LanguageNameKey(string language) => language switch
    {
        "Korean" => "NativeVocabQuiz_Language_Korean",
        "English" => "NativeVocabQuiz_Language_English",
        "French" => "NativeVocabQuiz_Language_French",
        "German" => "NativeVocabQuiz_Language_German",
        "Spanish" => "NativeVocabQuiz_Language_Spanish",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    // ------------------------------------------------------------ More screen controls

    /// <summary>A labelled select (source &lt;select&gt;): an outlined anchor and a Material menu.</summary>
    private ComposableNode MoreDropdown(string key, string tag, string label,
        IReadOnlyList<(string Value, string Label)> options, string selected,
        Action<string> changed, bool enabled, AndroidVisualPalette palette)
    {
        var selectedIndex = -1;
        for (var index = 0; index < options.Count; index++)
            if (string.Equals(options[index].Value, selected, StringComparison.Ordinal))
                selectedIndex = index;
        var shown = selectedIndex >= 0 ? options[selectedIndex].Label
            : options.Count > 0 ? options[0].Label : string.Empty;
        var anchor = new OutlinedButton(() => ToggleMoreMenu(key), enabled: enabled)
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(tag)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription($"{label}: {shown}");
                    semantics.StateDescription(shown);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        anchor.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Text(shown)
            {
                Modifier = Modifier.Weight(1f),
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
                Color = palette.Text,
            },
            new Icon(Icons.Default.ArrowDropDown, label)
            {
                Modifier = Modifier.Size(20),
                Tint = palette.Text,
            },
        });
        var menu = new DropdownMenu(expanded: _moreMenu == key, onDismissRequest: CloseMoreMenu)
        {
            Modifier = PopupWindowSemantics(Modifier.TestTag($"{tag}.menu")),
        };
        for (var index = 0; index < options.Count; index++)
        {
            var (value, text) = options[index];
            var isSelected = index == selectedIndex;
            menu.Add(new DropdownMenuItem(text: new Text(text), onClick: () =>
            {
                CloseMoreMenu();
                changed(value);
            })
            {
                Modifier = Modifier.TestTag($"{tag}.option.{index}").Semantics(semantics =>
                {
                    semantics.ContentDescription(text);
                    semantics.Selected(isSelected);
                }),
                TrailingIcon = isSelected
                    ? new Icon(Icons.Default.Check, text) { Modifier = Modifier.Size(20), Tint = palette.Text }
                    : null,
            });
        }
        return new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth(),
            Caption(label, palette),
            new Box { Modifier.FillMaxWidth(), anchor, menu },
        };
    }

    /// <summary>A single-choice segmented control (source btn-group / iOS segmented picker).</summary>
    private static ComposableNode MoreSegments(string tag,
        IReadOnlyList<(string Value, string Label)> options, string selected,
        Action<string> changed, bool enabled, bool allowReselect = false)
    {
        var row = new SingleChoiceSegmentedButtonRow
        {
            Modifier = Modifier.FillMaxWidth().TestTag(tag),
        };
        foreach (var (value, label) in options)
        {
            var isSelected = string.Equals(value, selected, StringComparison.Ordinal);
            var button = new SegmentedButton(selected: isSelected, onClick: () =>
            {
                if (enabled && (allowReselect || !isSelected))
                    changed(value);
            })
            {
                Modifier = Modifier.TestTag($"{tag}.{value}").Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Selected(isSelected);
                }),
            };
            button.Add(new Text(label) { MaxLines = 1, Overflow = TextOverflow.Ellipsis });
            row.Add(button);
        }
        return row;
    }

    private static ComposableNode SettingSwitch(string label, string tag, bool value,
        Action<bool> changed, bool enabled, AndroidVisualPalette palette) =>
        new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight),
            Body(label, palette, modifier: Modifier.Weight(1f)),
            new AndroidX.Compose.Switch(value, changed, enabled)
            {
                Modifier = Modifier.TestTag(tag)
                    .Semantics(semantics => semantics.ContentDescription(label)),
            },
        };
}
