namespace SentenceStudio.Shared.Services;

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
    ///   5. The user has NOT toggled "show text with photo" on (showTextOverride == false).
    ///
    /// Target-language prompts are NEVER hidden — seeing the script alongside the
    /// image builds recognition. Hiding native text is acceptable because matching a
    /// photo to a native-language word alone has no learning value.
    /// </summary>
    public static bool ShouldHideText(
        bool hasImage,
        bool photoPromptActive,
        bool imageVisible,
        bool promptIsNativeLanguage,
        bool showTextOverride)
    {
        return hasImage
            && photoPromptActive
            && imageVisible
            && promptIsNativeLanguage
            && !showTextOverride;
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
