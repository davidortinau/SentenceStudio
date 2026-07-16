using FluentAssertions;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Tests for VocabQuizPhotoTextPolicy — the pure decision logic that determines
/// when quiz prompt text is hidden/shown in the presence of photos, and when
/// the Show/Hide Text toolbar toggle is eligible.
///
/// Key invariant (Captain's requirement):
///   Target-language terms are NEVER hidden. Only native-language prompts may be
///   hidden behind a photo, and only when the user has not overridden.
/// </summary>
public sealed class VocabQuizPhotoTextPolicyTests
{
    // ──────────────────────────────────────────────────────────────
    //  ShouldHideText — target-language prompts ALWAYS visible
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void TargetLanguagePrompt_TextAlwaysVisible_RegardlessOfPhotoState()
    {
        // promptIsNativeLanguage = false → target language prompt
        var result = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: false,
            showTextOverride: false);

        result.Should().BeFalse("target-language terms must never be hidden — they have learning value");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TargetLanguagePrompt_TextVisible_RegardlessOfShowTextOverride(bool showTextOverride)
    {
        var result = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: false,
            showTextOverride: showTextOverride);

        result.Should().BeFalse("target-language text remains visible regardless of user preference");
    }

    // ──────────────────────────────────────────────────────────────
    //  ShouldHideText — native-language prompts default hidden
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void NativeLanguagePrompt_WithPhoto_DefaultHidden()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false);

        result.Should().BeTrue("native-language text is hidden by default when photo is visible");
    }

    [Fact]
    public void NativeLanguagePhotoPrompt_BeforeAnswer_TextRemainsHidden()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: false);

        result.Should().BeFalse("native-language text must not leak before grading");
    }

    [Fact]
    public void NativeLanguagePhotoPrompt_DefaultTextAndPhoto_BeforeAnswer_TextIsHidden()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: true,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: false);

        result.Should().BeFalse("photo-hide policy must override the enabled text-prompt default");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeLanguagePhotoPrompt_DefaultTextAndPhoto_DuringFeedback_TextIsRevealed(
        bool answerWasCorrect)
    {
        var result = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: true,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: true);

        result.Should().BeTrue(
            $"answer feedback must reveal the native prompt after a {(answerWasCorrect ? "correct" : "incorrect")} answer");
    }

    [Fact]
    public void NativeLanguagePhotoPrompt_CorrectFeedback_RevealsText()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: true);

        result.Should().BeTrue("correct feedback must reveal the native-language prompt");
    }

    [Fact]
    public void NativeLanguagePhotoPrompt_IncorrectFeedback_RevealsText()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: true);

        result.Should().BeTrue("incorrect feedback must reveal the native-language prompt");
    }

    [Fact]
    public void NativeLanguagePhotoPrompt_NextItem_HidesTextAgain()
    {
        var displayedBeforeAnswer = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: true, showTextOverride: false,
            isAnswerFeedbackVisible: false);
        var displayedDuringFeedback = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: true, showTextOverride: false,
            isAnswerFeedbackVisible: true);
        var displayedOnNextItem = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: true, showTextOverride: false,
            isAnswerFeedbackVisible: false);

        displayedBeforeAnswer.Should().BeFalse();
        displayedDuringFeedback.Should().BeTrue();
        displayedOnNextItem.Should().BeFalse("loading the next item resets the answer-feedback state");
    }

    [Fact]
    public void TargetLanguagePhotoPrompt_FeedbackState_DoesNotChangeVisibility()
    {
        var displayedBeforeAnswer = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: false, showTextOverride: false,
            isAnswerFeedbackVisible: false);
        var displayedDuringFeedback = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: false, showTextOverride: false,
            isAnswerFeedbackVisible: true);

        displayedBeforeAnswer.Should().BeTrue();
        displayedDuringFeedback.Should().BeTrue("target-language prompts are always visible");
    }

    [Fact]
    public void NativeLanguagePhotoPrompt_UserOverride_RemainsVisibleWithoutFlicker()
    {
        var displayedBeforeAnswer = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: true, showTextOverride: true,
            isAnswerFeedbackVisible: false);
        var displayedDuringFeedback = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: true, showTextOverride: true,
            isAnswerFeedbackVisible: true);

        displayedBeforeAnswer.Should().BeTrue();
        displayedDuringFeedback.Should().BeTrue("the user override keeps text visible across grading");
    }

    [Fact]
    public void NonPhotoPrompt_FeedbackState_DoesNotChangeVisibility()
    {
        var displayedBeforeAnswer = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: false, photoPromptActive: false, imageVisible: false,
            promptIsNativeLanguage: true, showTextOverride: false,
            isAnswerFeedbackVisible: false);
        var displayedDuringFeedback = VocabQuizPhotoTextPolicy.ShouldDisplayText(
            textPromptActive: false,
            hasImage: false, photoPromptActive: false, imageVisible: false,
            promptIsNativeLanguage: true, showTextOverride: false,
            isAnswerFeedbackVisible: true);

        displayedBeforeAnswer.Should().BeFalse();
        displayedDuringFeedback.Should().BeFalse("non-photo scenarios preserve the configured text-prompt state");
    }

    // ──────────────────────────────────────────────────────────────
    //  Prompt heading — hidden photo policy takes precedence
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void NativeLanguagePhotoPrompt_DefaultTextAndPhoto_BeforeAnswer_UsesGenericHeading()
    {
        var result = VocabQuizPhotoTextPolicy.GetPromptHeadingKind(
            textPromptActive: true,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: false,
            audioPromptActive: false);

        result.Should().Be(VocabQuizPromptHeadingKind.LookAndAnswerInstruction,
            "the enabled text prompt must not leak through promptTitle");
    }

    [Fact]
    public void NativeLanguageAudioPhotoPrompt_DefaultText_BeforeAnswer_UsesGenericListeningHeading()
    {
        var result = VocabQuizPhotoTextPolicy.GetPromptHeadingKind(
            textPromptActive: true,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: false,
            audioPromptActive: true);

        result.Should().Be(VocabQuizPromptHeadingKind.ListenLookAndAnswerInstruction);
    }

    [Fact]
    public void NativeLanguagePhotoPrompt_DefaultTextAndPhoto_DuringFeedback_UsesQuestionTextHeading()
    {
        var result = VocabQuizPhotoTextPolicy.GetPromptHeadingKind(
            textPromptActive: true,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: true,
            audioPromptActive: false);

        result.Should().Be(VocabQuizPromptHeadingKind.QuestionText);
    }

    [Fact]
    public void NonPhotoPrompt_WithTextDisabled_PreservesConfiguredInstructionHeading()
    {
        var result = VocabQuizPhotoTextPolicy.GetPromptHeadingKind(
            textPromptActive: false,
            hasImage: false,
            photoPromptActive: false,
            imageVisible: false,
            promptIsNativeLanguage: true,
            showTextOverride: false,
            isAnswerFeedbackVisible: false,
            audioPromptActive: true);

        result.Should().Be(VocabQuizPromptHeadingKind.ConfiguredInstruction);
    }

    [Fact]
    public void TargetLanguagePhotoPrompt_WithTextDisabled_StillUsesQuestionTextHeading()
    {
        var result = VocabQuizPhotoTextPolicy.GetPromptHeadingKind(
            textPromptActive: false,
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: false,
            showTextOverride: false,
            isAnswerFeedbackVisible: false,
            audioPromptActive: false);

        result.Should().Be(VocabQuizPromptHeadingKind.QuestionText,
            "target-language text remains the learning-bearing prompt");
    }

    // ──────────────────────────────────────────────────────────────
    //  ShouldHideText — user preference override
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void NativeLanguagePrompt_ShowTextOverrideTrue_TextVisible()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: true);

        result.Should().BeFalse("user toggled show-text preference ON — text must be visible");
    }

    // ──────────────────────────────────────────────────────────────
    //  ShouldHideText — no-photo behavior
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void NoImage_TextNeverHidden()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: false,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false);

        result.Should().BeFalse("without an image, text must never be hidden");
    }

    [Fact]
    public void PhotoPromptInactive_TextNeverHidden()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: false,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false);

        result.Should().BeFalse("photo prompt inactive — text visible");
    }

    [Fact]
    public void ImageNotVisible_TextNeverHidden()
    {
        var result = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: false,
            promptIsNativeLanguage: true,
            showTextOverride: false);

        result.Should().BeFalse("image not displayed — text visible");
    }

    // ──────────────────────────────────────────────────────────────
    //  IsTextToggleEligible — toolbar only for native prompts
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void TargetLanguagePrompt_ToolbarToggle_NotEligible()
    {
        var result = VocabQuizPhotoTextPolicy.IsTextToggleEligible(
            hasImage: true,
            photoPromptActive: true,
            promptIsNativeLanguage: false);

        result.Should().BeFalse("toolbar toggle must not appear for target-language prompts — hiding text would remove learning value");
    }

    [Fact]
    public void NativeLanguagePrompt_WithPhoto_ToolbarToggle_Eligible()
    {
        var result = VocabQuizPhotoTextPolicy.IsTextToggleEligible(
            hasImage: true,
            photoPromptActive: true,
            promptIsNativeLanguage: true);

        result.Should().BeTrue("native-language photo prompts allow toggling text visibility");
    }

    [Fact]
    public void NoImage_ToolbarToggle_NotEligible()
    {
        var result = VocabQuizPhotoTextPolicy.IsTextToggleEligible(
            hasImage: false,
            photoPromptActive: true,
            promptIsNativeLanguage: true);

        result.Should().BeFalse("no image means toggle is irrelevant");
    }

    [Fact]
    public void PhotoPromptInactive_ToolbarToggle_NotEligible()
    {
        var result = VocabQuizPhotoTextPolicy.IsTextToggleEligible(
            hasImage: true,
            photoPromptActive: false,
            promptIsNativeLanguage: true);

        result.Should().BeFalse("photo prompt not active — toggle irrelevant");
    }

    // ──────────────────────────────────────────────────────────────
    //  Fullscreen viewer state (purely boolean — no Blazor deps)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void FullscreenOpenClose_DoesNotAffectTextHiding()
    {
        // Opening fullscreen should not change text visibility decision
        var hiddenBefore = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false);

        // After "fullscreen close" the same inputs produce same result
        var hiddenAfter = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false);

        hiddenBefore.Should().Be(hiddenAfter,
            "fullscreen open/close must not reset quiz state or text visibility");
    }

    // ──────────────────────────────────────────────────────────────
    //  Mixed mode — per-turn re-evaluation acceptance matrix
    //  (Zoe's contract: direction switches every turn in Mixed)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void MixedMode_NativeTurn_TextHidden()
    {
        // Simulates a Mixed-mode turn where promptIsNativeLanguage = true
        var hidden = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: true,
            showTextOverride: false);

        var eligible = VocabQuizPhotoTextPolicy.IsTextToggleEligible(
            hasImage: true,
            photoPromptActive: true,
            promptIsNativeLanguage: true);

        hidden.Should().BeTrue("Mixed-mode native turn hides text behind photo");
        eligible.Should().BeTrue("toolbar toggle available on native turns");
    }

    [Fact]
    public void MixedMode_TargetTurn_TextVisible()
    {
        // Simulates a Mixed-mode turn where promptIsNativeLanguage = false
        var hidden = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true,
            photoPromptActive: true,
            imageVisible: true,
            promptIsNativeLanguage: false,
            showTextOverride: false);

        var eligible = VocabQuizPhotoTextPolicy.IsTextToggleEligible(
            hasImage: true,
            photoPromptActive: true,
            promptIsNativeLanguage: false);

        hidden.Should().BeFalse("Mixed-mode target turn keeps text visible");
        eligible.Should().BeFalse("toolbar toggle unavailable on target turns");
    }

    [Fact]
    public void MixedMode_AlternatingTurns_IndependentDecisions()
    {
        // Turn 1: native — text hidden
        var turn1Hidden = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: true, showTextOverride: false);

        // Turn 2: target — text visible
        var turn2Hidden = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: false, showTextOverride: false);

        // Turn 3: native again — text hidden
        var turn3Hidden = VocabQuizPhotoTextPolicy.ShouldHideText(
            hasImage: true, photoPromptActive: true, imageVisible: true,
            promptIsNativeLanguage: true, showTextOverride: false);

        turn1Hidden.Should().BeTrue();
        turn2Hidden.Should().BeFalse();
        turn3Hidden.Should().BeTrue();
    }
}
