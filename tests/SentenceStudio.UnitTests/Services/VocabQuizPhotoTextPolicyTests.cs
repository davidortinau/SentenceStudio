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
