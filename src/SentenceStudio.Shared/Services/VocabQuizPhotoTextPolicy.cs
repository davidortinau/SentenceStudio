namespace SentenceStudio.Shared.Services;

public enum VocabQuizPromptHeadingKind
{
    QuestionText,
    ConfiguredInstruction,
    LookAndAnswerInstruction,
    ListenLookAndAnswerInstruction
}

/// <summary>
/// Pure decision logic for the vocabulary quiz photo/text interaction.
/// Extracted from VocabQuiz.razor so it can be unit-tested without Blazor dependencies.
/// </summary>
public static class VocabQuizPhotoTextPolicy
{
    /// <summary>
    /// Determines whether quiz prompt text should be hidden in favour of the photo.
    /// Text is hidden ONLY when:
    ///   1. A photo is available (hasImage),
    ///   2. The photo prompt control is active (photoPromptActive),
    ///   3. The photo is currently shown (imageVisible),
    ///   4. The prompt is in the learner's NATIVE language (promptIsNativeLanguage),
    ///   5. The user has NOT toggled "show text with photo" on (showTextOverride == false),
    ///   6. Answer feedback is not visible yet (isAnswerFeedbackVisible == false).
    ///
    /// Target-language prompts are NEVER hidden — seeing the script alongside the
    /// image builds recognition. Native-language text is hidden only during retrieval,
    /// then revealed during both correct and incorrect feedback.
    /// </summary>
    public static bool ShouldHideText(
        bool hasImage,
        bool photoPromptActive,
        bool imageVisible,
        bool promptIsNativeLanguage,
        bool showTextOverride,
        bool isAnswerFeedbackVisible = false)
    {
        return !ShouldDisplayText(
            textPromptActive: true,
            hasImage: hasImage,
            photoPromptActive: photoPromptActive,
            imageVisible: imageVisible,
            promptIsNativeLanguage: promptIsNativeLanguage,
            showTextOverride: showTextOverride,
            isAnswerFeedbackVisible: isAnswerFeedbackVisible);
    }

    /// <summary>
    /// Determines whether the prompt term itself should be displayed.
    /// Non-photo scenarios preserve the configured text-prompt state. With a visible
    /// photo, target-language text remains visible, while native-language text is
    /// revealed by either the user override or answer feedback.
    /// </summary>
    public static bool ShouldDisplayText(
        bool textPromptActive,
        bool hasImage,
        bool photoPromptActive,
        bool imageVisible,
        bool promptIsNativeLanguage,
        bool showTextOverride,
        bool isAnswerFeedbackVisible)
    {
        if (!hasImage || !photoPromptActive || !imageVisible)
            return textPromptActive;

        if (!promptIsNativeLanguage)
            return true;

        return showTextOverride || isAnswerFeedbackVisible;
    }

    /// <summary>
    /// Selects the heading source while preserving the photo-hide rule ahead of the
    /// configured text-prompt preference. This prevents promptTitle from leaking the
    /// native term when UseTextPrompt and UsePhotoPrompt are both enabled.
    /// </summary>
    public static VocabQuizPromptHeadingKind GetPromptHeadingKind(
        bool textPromptActive,
        bool hasImage,
        bool photoPromptActive,
        bool imageVisible,
        bool promptIsNativeLanguage,
        bool showTextOverride,
        bool isAnswerFeedbackVisible,
        bool audioPromptActive)
    {
        if (ShouldHideText(
            hasImage,
            photoPromptActive,
            imageVisible,
            promptIsNativeLanguage,
            showTextOverride,
            isAnswerFeedbackVisible))
        {
            return audioPromptActive
                ? VocabQuizPromptHeadingKind.ListenLookAndAnswerInstruction
                : VocabQuizPromptHeadingKind.LookAndAnswerInstruction;
        }

        return ShouldDisplayText(
            textPromptActive,
            hasImage,
            photoPromptActive,
            imageVisible,
            promptIsNativeLanguage,
            showTextOverride,
            isAnswerFeedbackVisible)
                ? VocabQuizPromptHeadingKind.QuestionText
                : VocabQuizPromptHeadingKind.ConfiguredInstruction;
    }

    /// <summary>
    /// Determines whether the "Show/Hide Text" toolbar toggle is eligible to appear.
    /// The toggle is meaningful ONLY for native-language photo prompts — for target-language
    /// prompts the text must always remain visible, so the toggle must not appear.
    /// </summary>
    public static bool IsTextToggleEligible(
        bool hasImage,
        bool photoPromptActive,
        bool promptIsNativeLanguage)
    {
        return hasImage && photoPromptActive && promptIsNativeLanguage;
    }
}
