using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Converts existing quiz preferences and the shared photo/text policy to a
/// platform-neutral question contract. A blocked turn cannot be submitted.
/// </summary>
public static class NativeQuizModality
{
    public static bool CanPresentWithoutMedia(
        bool promptUsesNativeLanguage,
        string mode,
        VocabularyQuizPreferences preferences,
        bool hasPhoto,
        bool showTextWithPhoto) =>
        CanPresent(promptUsesNativeLanguage, mode, preferences, hasPhoto,
            showTextWithPhoto, audioAvailable: false, photoRendered: false);

    public static bool CanPresent(
        bool promptUsesNativeLanguage,
        string mode,
        VocabularyQuizPreferences preferences,
        bool hasPhoto,
        bool showTextWithPhoto,
        bool audioAvailable,
        bool photoRendered)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        return RequiredPromptMediaAvailable(promptUsesNativeLanguage, preferences,
            hasPhoto, showTextWithPhoto, audioAvailable, photoRendered);
    }

    private static bool RequiredPromptMediaAvailable(
        bool promptUsesNativeLanguage,
        VocabularyQuizPreferences preferences,
        bool hasPhoto,
        bool showTextWithPhoto,
        bool audioAvailable,
        bool photoRendered)
    {
        var written = preferences.UseTextPrompt
            || !(preferences.UseAudioPrompt && audioAvailable)
                && !(preferences.UsePhotoPrompt && hasPhoto && photoRendered);
        var writtenExposure = written
            && (!promptUsesNativeLanguage || !hasPhoto
                || !preferences.UsePhotoPrompt || !photoRendered || showTextWithPhoto);
        return writtenExposure
            || preferences.UseAudioPrompt && audioAvailable
            || preferences.UsePhotoPrompt && hasPhoto && photoRendered;
    }

    public static NativeQuizSentencePrompt RenderSentence(
        VocabularyWord word, string targetLanguage)
    {
        if (!FocusVocabularySelection.IsUsableVocabularyWord(word))
            throw new ArgumentException("A sentence prompt needs a target word and native gloss.", nameof(word));
        return new NativeQuizSentencePrompt(
            word.Id, word.TargetLanguageTerm!, word.NativeLanguageTerm!, targetLanguage);
    }

    public static NativeQuizPrompt Render(
        VocabularyWord word,
        bool promptUsesNativeLanguage,
        VocabularyQuizPreferences preferences,
        bool showTextWithPhoto,
        bool audioAvailable,
        bool audioHeard,
        bool photoLoaded,
        bool photoVisible,
        bool targetResponseVisible,
        string nativeLanguage,
        string targetLanguage,
        bool isAnswerFeedbackVisible = false)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(preferences);

        var usePhoto = preferences.UsePhotoPrompt && photoLoaded && photoVisible;
        var useAudio = preferences.UseAudioPrompt && audioAvailable;
        var written = preferences.UseTextPrompt
            || !useAudio && !usePhoto;

        var showText = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            written,
            photoLoaded,
            preferences.UsePhotoPrompt,
            usePhoto,
            promptUsesNativeLanguage,
            showTextWithPhoto,
            isAnswerFeedbackVisible);

        var promptTerm = promptUsesNativeLanguage
            ? word.NativeLanguageTerm
            : word.TargetLanguageTerm;
        var promptLanguage = promptUsesNativeLanguage
            ? nativeLanguage : word.Language ?? targetLanguage;
        var meaningfulCue = showText || usePhoto || useAudio && audioHeard;
        var mediaReady = RequiredPromptMediaAvailable(
            promptUsesNativeLanguage, preferences,
            !string.IsNullOrWhiteSpace(word.MnemonicImageUri),
            showTextWithPhoto, audioAvailable, usePhoto);
        var l2Engaged = mediaReady && (promptUsesNativeLanguage
            ? targetResponseVisible && meaningfulCue
            : showText || useAudio && audioHeard);

        return new NativeQuizPrompt(
            word.Id,
            showText ? promptTerm : null,
            promptLanguage,
            useAudio ? promptTerm : null,
            usePhoto ? word.MnemonicImageUri : null,
            IsTargetLanguagePrompt: !promptUsesNativeLanguage,
            ShowWrittenPrompt: showText,
            CanPlayPromptAudio: useAudio,
            ShowPhoto: usePhoto,
            TargetLanguageEngaged: l2Engaged);
    }
}

public enum NativeQuizTimingUpdate { Applied, Invalid, RequiresSharedPreferenceFix }

public static class NativeQuizTiming
{
    /// <summary>The 19 approved settings values: one through ten seconds, half-second steps.</summary>
    public static IReadOnlyList<int> ApprovedDurationsMs { get; } =
        Enumerable.Range(0, 19).Select(step => 1000 + step * 500).ToArray();

    /// <summary>
    /// Only report an approved duration as applied when the shared preference
    /// actually persists that duration.
    /// </summary>
    public static NativeQuizTimingUpdate TrySet(
        VocabularyQuizPreferences preferences, int milliseconds)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!ApprovedDurationsMs.Contains(milliseconds))
            return NativeQuizTimingUpdate.Invalid;
        preferences.AutoAdvanceDuration = milliseconds;
        return preferences.AutoAdvanceDuration == milliseconds
            ? NativeQuizTimingUpdate.Applied
            : NativeQuizTimingUpdate.RequiresSharedPreferenceFix;
    }
}
