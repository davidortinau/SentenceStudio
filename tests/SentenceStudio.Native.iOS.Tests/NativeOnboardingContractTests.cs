using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeOnboardingContractTests
{
    /// <summary>
    /// AF-N04: source Onboarding.razor:59-75,277-283,311-329 (names load when the target
    /// language step is left; a grid of name buttons fills the field) and AF-D01: source
    /// Index.razor:804-811 (Quick Start with no target language goes to onboarding).
    /// </summary>
    [Fact]
    public void NameStepOffersSuggestedNamesAndQuickStartRoutesToOnboarding()
    {
        var view = Source("Features/Onboarding/OnboardingView.cs");
        var app = Source("App/ApplicationController.cs");
        var onboarding = Source("App/ApplicationController.Onboarding.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("Onboarding_GeneratingNames", view);
        Assert.Contains("Onboarding_OrChooseSuggested", view);
        Assert.Contains("Views.LazyVGrid(2, buttons)", view);
        Assert.Contains("native.onboarding.names.", view);
        Assert.Contains("suggestions.Choose(name)", view);
        Assert.Contains("if (_onboardingStep == 2)\n            LoadOnboardingNames(_onboardingDraft.TargetLanguage);",
            app);
        Assert.Contains("auth.SuggestOnboardingNamesAsync(targetLanguage, load.Token)", onboarding);
        // Not async void (AGENTS.md): the load is a Task that observes its own failures.
        Assert.DoesNotContain("async void", onboarding);
        Assert.Contains("_ = LoadOnboardingNamesAsync(auth, targetLanguage, load);", onboarding);
        Assert.Contains("private async Task LoadOnboardingNamesAsync(", onboarding);
        Assert.Contains("_onboardingDraft = _onboardingDraft with { Name = name };", onboarding);
        // Names arriving while the learner types keep the field focused.
        Assert.Contains("var focusedId = FindFocusedInput(_onboardingSlot!)?.AccessibilityIdentifier;",
            scene);

        Assert.Contains("_auth.BeginOnboardingForMissingTargetLanguageAsync(_lifetime.Token)", onboarding);
        Assert.Contains("if (!await RouteToOnboardingForMissingTargetLanguageAsync())", app);
        Assert.Contains("created.ErrorKey == \"NativeResources_SetTargetLanguage\"\n"
            + "                && await RouteToOnboardingForMissingTargetLanguageAsync()", app);
        // The source progress card: done, in progress (spinner) or waiting.
        Assert.Contains("Views.IndeterminateProgress(label)", view);
    }

    [Fact]
    public void SixStepNativeOnboarding_FinishesThroughOwnerCheckedPresenterSave()
    {
        var view = Source("Features/Onboarding/OnboardingView.cs");
        var app = Source("App/ApplicationController.cs");
        var scene = Source("App/ApplicationScene.cs");

        for (var step = 0; step < 6; step++)
            Assert.Contains($"case {step}:", view);
        Assert.Contains("NativeOnboardingOptions.NativeLanguages", view);
        Assert.Contains("NativeOnboardingOptions.TargetLanguages", view);
        Assert.Contains("NativeOnboardingOptions.SessionMinutes", view);
        Assert.Contains("NativeOnboardingOptions.CefrLevels", view);
        Assert.Contains("native.onboarding.name", view);
        Assert.Contains("Onboarding_CreateStarter", view);
        Assert.Contains("Onboarding_SkipSetup", view);
        Assert.Contains("native.onboarding.signout", view);
        Assert.DoesNotContain("native.onboarding.finish.unavailable", view);
        Assert.DoesNotContain("Coach_Hint_Unknown", view);
        Assert.Contains("SetCustomButtonAvailability(starter, style.Appearance,",
            view);
        Assert.Contains("SetCustomButtonAvailability(skip, style.Appearance,",
            view);
        Assert.Contains("SetCustomButtonAvailability(nextButton, style.Appearance,",
            view);
        Assert.Contains("var fullWidth = groupPosition == SourceButtonGroupPosition.None;",
            view);
        Assert.Contains("style.PrimaryButton(label, action, fullWidth: fullWidth,",
            view);
        Assert.Contains("style.SecondaryButton(label, action, fullWidth: fullWidth,",
            view);
        Assert.DoesNotContain("Views.Image(selected ? \"checkmark.circle.fill\"", view);
        Assert.Contains("Views.Scroll(column)", view);
        Assert.Contains("Views.Circle(8, color.Light, color.Dark)", view);
        Assert.Contains("native.onboarding.step", view);
        Assert.Contains("dots.Spacing = 4;", view);
        Assert.Contains("Views.VStack(_app.RenderOnboarding(_style))", scene);
        Assert.Contains("registerNext: next => _onboardingNextButton = next", app);
        Assert.Contains("SetCustomButtonAvailability(", app);
        // Profile durability is now the presenter's remote write-through,
        // not a permanently disabled finish button.
        Assert.DoesNotContain("OnboardingDurabilityAvailable", app);
        Assert.DoesNotContain("Coach_Hint_Unknown", app);
        Assert.Contains("_auth.SaveOnboardingProfileAsync(", app);
        Assert.Contains("_auth.FinishOnboardingAsync(", app);
        Assert.Contains("GetByIdAsync(owner, _lifetime.Token)", app);
        Assert.Contains("NativeDestination.Onboarding", app);
        Assert.Contains("SetOnboardingFailure(", app);
        Assert.DoesNotContain("UserProfileRepository.SaveAsync(", app);
        Assert.DoesNotContain(
            "Phase = NativePhase.Blocked, ErrorKey = NativeText.SignInUnavailable",
            app);
    }

    [Fact]
    public void LanguageAndPreferenceChoices_KeepOriginalSourceOptions()
    {
        Assert.Equal(
        [
            "English", "Korean", "French", "German", "Spanish",
            "Japanese", "Chinese"
        ], NativeOnboardingOptions.NativeLanguages.ToArray());
        Assert.Equal(
        [
            "Korean", "English", "French", "German", "Spanish",
            "Japanese", "Chinese"
        ], NativeOnboardingOptions.TargetLanguages.ToArray());
        Assert.Equal([5, 10, 15, 20, 30, 45],
            NativeOnboardingOptions.SessionMinutes.ToArray());
        Assert.Equal(["A1", "A2", "B1", "B2", "C1", "C2"],
            NativeOnboardingOptions.CefrLevels.ToArray());
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void NativeStepActionsAndFailureStates_UseExistingTranslatedCopy(
        string resourceFile)
    {
        var path = Path.GetFullPath(Path.Combine(HeadPath,
            "../SentenceStudio.Shared/Resources/Strings", resourceFile));
        var strings = XDocument.Load(path).Descendants("data").ToArray();
        foreach (var key in new[]
        {
            "Onboarding_Welcome", "Onboarding_GetStarted",
            "Onboarding_NativeLanguage", "Onboarding_TargetLanguage",
            "Onboarding_YourName", "Onboarding_EnterName",
            "Onboarding_LearningPreferences", "Onboarding_StudyDuration",
            "Onboarding_TargetLevel", "Onboarding_AllSet",
            "Onboarding_CreateStarter", "Onboarding_SkipSetup",
            "Onboarding_Back", "Onboarding_Next", "Nav_Logout",
            "NativeOnboarding_Incomplete", "NativeOnboarding_SaveFailed",
            "NativeOnboarding_StarterFailed"
        })
            Assert.True(strings.Any(data =>
                    data.Attribute("name")?.Value == key
                    && !string.IsNullOrWhiteSpace(data.Element("value")?.Value)),
                $"{resourceFile} lacks {key}");
    }

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!,
        "../../src/SentenceStudio.Native.iOS"));

    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(HeadPath, name));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
