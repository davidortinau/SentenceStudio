using System.Globalization;
using System.Collections.Immutable;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

/// <summary>
/// The Voice &amp; Quiz settings as Form Sections (Captain 2026-11-13, the native form pattern):
/// the voice pickers, the prompt direction and switches; the prompt modalities; the
/// auto-advance slider and Save Preferences as the last row.
/// </summary>
internal static class QuizSettingsView
{
    internal static IEnumerable<SwiftView> Sections(
        SourceUiStyle style, QuizSettingsDraft quiz,
        Func<string, string> text, Action<string> selectVoiceLanguage,
        Action<Func<QuizSettingsDraft, QuizSettingsDraft>> quizChanged,
        Action saveQuiz, bool voiceLoading, string? voiceErrorKey)
    {
        var tint = style.Appearance.Primary;
        var languages = Views.Picker(text("Settings_LanguageLabel"),
                NativeQuizSettings.Languages.Select(language =>
                    new SwiftPickerOption(language,
                        LanguageName(language, text))).ToArray(),
                quiz.VoiceLanguage, selectVoiceLanguage)
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithTintColor(tint.Light, tint.Dark)
            .WithAccessibilityIdentifier("native.settings.quiz.voice-language");
        var voice = new List<SwiftView> { languages };
        if (voiceLoading)
            voice.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.settings.quiz.voices.loading"));
        else if (!quiz.VoiceOptions.IsDefaultOrEmpty
            || !string.IsNullOrWhiteSpace(quiz.VoiceId))
            voice.Add(Views.Picker(text("Settings_VoiceLabel"),
                    (quiz.VoiceOptions.IsDefaultOrEmpty
                        ? ImmutableArray.Create(new NativeQuizVoiceOption(
                            quiz.VoiceId, text("NativeSettings_CurrentVoice")))
                        : quiz.VoiceOptions).Select(option =>
                        new SwiftPickerOption(option.Id, option.Name)).ToArray(),
                    quiz.VoiceId,
                    value => quizChanged(draft => draft with { VoiceId = value }))
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithTintColor(tint.Light, tint.Dark)
                .WithAccessibilityIdentifier("native.settings.quiz.voice"));
        if (voiceErrorKey is not null)
            voice.Add(style.Text(Views.Text(text(voiceErrorKey)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.settings.quiz.voices.error"));

        // The prompt direction: its label above the segmented control, the help under it.
        var direction = Views.VStack(
                style.Text(Views.Text(text("Settings_PromptLanguageLabel")), SourceTextRole.Body1),
                Views.Picker(text("Settings_PromptLanguageLabel"),
                    [
                        new SwiftPickerOption("TargetToNative",
                            text("Settings_TargetLanguage")),
                        new SwiftPickerOption("NativeToTarget",
                            text("Settings_NativeLanguage")),
                        new SwiftPickerOption("Mixed", text("Settings_Mixed")),
                    ], quiz.Direction,
                    value => quizChanged(draft => draft with { Direction = value }))
                .WithPickerStyle(SwiftPickerStyle.Segmented)
                .WithAccessibilityIdentifier("native.settings.quiz.direction"),
                style.Text(Views.Text(text("Settings_PromptLanguageHelp")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary))
            .WithFlexibleWidth();
        direction.Spacing = SourceUiLayout.ItemGap;
        voice.Add(direction);
        voice.Add(NativeForm.Switch(style, text("Settings_AutoplayAudio"), quiz.AutoplayAudio,
            enabled => quizChanged(draft => draft with { AutoplayAudio = enabled }),
            "native.settings.quiz.autoplay"));
        voice.Add(NativeForm.Switch(style, text("Settings_ShowMnemonics"), quiz.ShowMnemonics,
            enabled => quizChanged(draft => draft with { ShowMnemonics = enabled }),
            "native.settings.quiz.mnemonics"));
        yield return Views.Section(text("Settings_VoiceQuizSection"), voice.ToArray());

        yield return Views.Section(text("Settings_PromptModalitiesLabel"),
            NativeForm.Switch(style, text("Settings_TextModality"), quiz.TextPrompt,
                enabled => quizChanged(draft => draft with { TextPrompt = enabled }),
                "native.settings.quiz.text"),
            NativeForm.Switch(style, text("Settings_AudioModality"), quiz.AudioPrompt,
                enabled => quizChanged(draft => draft with { AudioPrompt = enabled }),
                "native.settings.quiz.audio"),
            NativeForm.Switch(style, text("Settings_PhotoModality"), quiz.PhotoPrompt,
                enabled => quizChanged(draft => draft with { PhotoPrompt = enabled }),
                "native.settings.quiz.photo"),
            style.Text(Views.Text(text("Settings_ModalitiesHelp")),
                SourceTextRole.Caption1, SourceTextTone.Secondary));

        var durationLabel = style.Text(
                Views.Text(DurationTitle(quiz.AutoAdvanceSeconds, text)),
                SourceTextRole.Body1)
            .WithAccessibilityIdentifier("native.settings.quiz.duration.label");
        SwiftView? durationSlider = null;
        durationSlider = Views.Slider(
                DurationTitle(quiz.AutoAdvanceSeconds, text), 1, 10,
                quiz.AutoAdvanceSeconds, raw =>
                {
                    var seconds = QuizSettingsDraft.SnapDuration(raw);
                    durationSlider!.Value = seconds;
                    durationLabel.Text = DurationTitle(seconds, text);
                    durationSlider.AccessibilityLabel = DurationTitle(seconds, text);
                    quizChanged(draft => draft with {
                        AutoAdvanceSeconds = seconds
                    });
                })
            .WithAccessibilityIdentifier("native.settings.quiz.duration");
        var duration = Views.VStack(durationLabel, durationSlider).WithFlexibleWidth();
        duration.Spacing = SourceUiLayout.ItemGap;
        var save = NativeForm.Action(style, text("Settings_SavePreferences"),
            "checkmark.circle", saveQuiz, "native.settings.quiz.save");
        save.IsEnabled = !voiceLoading;
        yield return Views.Section(string.Empty, duration, save);
    }

    private static string DurationTitle(
        double seconds, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture,
            text("Settings_AutoAdvanceDurationLabel"),
            seconds.ToString("0.#", CultureInfo.CurrentCulture));

    private static string LanguageName(
        string language, Func<string, string> text) => text(language switch
    {
        "Korean" => "NativeVocabQuiz_Language_Korean",
        "English" => "NativeVocabQuiz_Language_English",
        "French" => "NativeVocabQuiz_Language_French",
        "German" => "NativeVocabQuiz_Language_German",
        "Spanish" => "NativeVocabQuiz_Language_Spanish",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    });
}
