using System.Globalization;
using System.Collections.Immutable;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

internal static class QuizSettingsView
{
    internal static SwiftView Section(
        SourceUiStyle style, QuizSettingsDraft quiz,
        Func<string, string> text, Action<string> selectVoiceLanguage,
        Action<Func<QuizSettingsDraft, QuizSettingsDraft>> quizChanged,
        Action saveQuiz, bool voiceLoading, string? voiceErrorKey)
    {
        var directions = Views.Picker(text("Settings_PromptLanguageLabel"),
                [
                    new SwiftPickerOption("TargetToNative",
                        text("Settings_TargetLanguage")),
                    new SwiftPickerOption("NativeToTarget",
                        text("Settings_NativeLanguage")),
                    new SwiftPickerOption("Mixed", text("Settings_Mixed")),
                ], quiz.Direction,
                value => quizChanged(draft => draft with { Direction = value }))
            .WithPickerStyle(SwiftPickerStyle.Segmented)
            .WithAccessibilityIdentifier("native.settings.quiz.direction");

        var languages = Views.Picker(text("Settings_LanguageLabel"),
                NativeQuizSettings.Languages.Select(language =>
                    new SwiftPickerOption(language,
                        LanguageName(language, text))).ToArray(),
                quiz.VoiceLanguage, selectVoiceLanguage)
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithAccessibilityIdentifier("native.settings.quiz.voice-language");
        var voiceControls = new List<SwiftView> { languages };
        if (voiceLoading)
            voiceControls.Add(style.Text(Views.Text(text("Common_Loading")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.settings.quiz.voices.loading"));
        else if (!quiz.VoiceOptions.IsDefaultOrEmpty
            || !string.IsNullOrWhiteSpace(quiz.VoiceId))
            voiceControls.Add(Views.Picker(text("Settings_VoiceLabel"),
                    (quiz.VoiceOptions.IsDefaultOrEmpty
                        ? ImmutableArray.Create(new NativeQuizVoiceOption(
                            quiz.VoiceId, text("NativeSettings_CurrentVoice")))
                        : quiz.VoiceOptions).Select(option =>
                        new SwiftPickerOption(option.Id, option.Name)).ToArray(),
                    quiz.VoiceId,
                    value => quizChanged(draft => draft with { VoiceId = value }))
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithAccessibilityIdentifier("native.settings.quiz.voice"));
        if (voiceErrorKey is not null)
            voiceControls.Add(style.Text(Views.Text(text(voiceErrorKey)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.settings.quiz.voices.error"));

        var durationLabel = style.Text(
                Views.Text(DurationTitle(quiz.AutoAdvanceSeconds, text)),
                SourceTextRole.Body2)
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
        var save = style.PrimaryButton(text("Settings_SavePreferences"), saveQuiz)
            .WithAccessibilityIdentifier("native.settings.quiz.save");
        save.IsEnabled = !voiceLoading;
        var quizSettings = Views.VStack(
            style.Text(Views.Text(text("Settings_VoiceQuizSection")),
                SourceTextRole.Title3, sourceHeading: true),
            Views.VStack(voiceControls.ToArray()),
            directions,
            style.Text(Views.Text(text("Settings_PromptLanguageHelp")),
                SourceTextRole.Caption1, SourceTextTone.Secondary),
            Views.Toggle(text("Settings_AutoplayAudio"), quiz.AutoplayAudio,
                enabled => quizChanged(draft => draft with {
                    AutoplayAudio = enabled
                })).WithAccessibilityIdentifier("native.settings.quiz.autoplay"),
            Views.Toggle(text("Settings_ShowMnemonics"), quiz.ShowMnemonics,
                enabled => quizChanged(draft => draft with {
                    ShowMnemonics = enabled
                })).WithAccessibilityIdentifier("native.settings.quiz.mnemonics"),
            style.Text(Views.Text(text("Settings_PromptModalitiesLabel")),
                SourceTextRole.Body2Strong),
            Views.Toggle(text("Settings_TextModality"), quiz.TextPrompt,
                enabled => quizChanged(draft => draft with {
                    TextPrompt = enabled
                })).WithAccessibilityIdentifier("native.settings.quiz.text"),
            Views.Toggle(text("Settings_AudioModality"), quiz.AudioPrompt,
                enabled => quizChanged(draft => draft with {
                    AudioPrompt = enabled
                })).WithAccessibilityIdentifier("native.settings.quiz.audio"),
            Views.Toggle(text("Settings_PhotoModality"), quiz.PhotoPrompt,
                enabled => quizChanged(draft => draft with {
                    PhotoPrompt = enabled
                })).WithAccessibilityIdentifier("native.settings.quiz.photo"),
            style.Text(Views.Text(text("Settings_ModalitiesHelp")),
                SourceTextRole.Caption1, SourceTextTone.Secondary),
            durationLabel,
            durationSlider,
            save);
        quizSettings.Spacing = SourceUiLayout.PageGap;
        quizSettings.WithPadding(SourceUiLayout.CardInset);

        return style.Card(quizSettings);
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
