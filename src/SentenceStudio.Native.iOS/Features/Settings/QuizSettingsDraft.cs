using SentenceStudio.Contracts.Theme;
using System.Collections.Immutable;

namespace SentenceStudio.Native.iOS.Features.Settings;

internal sealed record QuizSettingsDraft(
    string Direction,
    bool AutoplayAudio,
    bool ShowMnemonics,
    bool TextPrompt,
    bool AudioPrompt,
    bool PhotoPrompt,
    double AutoAdvanceSeconds,
    string VoiceLanguage,
    string VoiceId,
    ImmutableArray<NativeQuizVoiceOption> VoiceOptions)
{
    internal static QuizSettingsDraft FromState(NativeQuizSettingsState state) =>
        new(state.Direction,
            state.AutoPlayAudio,
            state.ShowMnemonics,
            state.UseTextPrompt,
            state.UseAudioPrompt,
            state.UsePhotoPrompt,
            state.AutoAdvanceDurationMs / 1000d,
            state.VoiceLanguage,
            state.VoiceId,
            state.VoiceOptions);

    internal NativeQuizSettingsState ToState() =>
        new(Direction, AutoplayAudio, ShowMnemonics, TextPrompt,
            AudioPrompt, PhotoPrompt,
            (int)Math.Round(AutoAdvanceSeconds * 1000),
            VoiceLanguage, VoiceId) { VoiceOptions = VoiceOptions };

    internal QuizSettingsDraft WithValidModalities() =>
        TextPrompt || AudioPrompt || PhotoPrompt
            ? this : this with { TextPrompt = true };

    internal static double SnapDuration(double seconds) =>
        Math.Clamp(Math.Round(seconds * 2, MidpointRounding.AwayFromZero) / 2,
            1, 10);
}

internal static class SettingsInput
{
    internal static double SnapFontScale(double scale) =>
        Math.Clamp(Math.Round(scale * 20, MidpointRounding.AwayFromZero) / 20,
            AppearanceSelection.MinFontScale, AppearanceSelection.MaxFontScale);
}
