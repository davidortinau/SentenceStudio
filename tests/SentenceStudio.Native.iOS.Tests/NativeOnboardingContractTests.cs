using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeOnboardingContractTests
{
    [Fact]
    public void SixStepNativeOnboarding_GatesTypedSaveAndFinishOnRemoteDurability()
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
        Assert.Contains("native.onboarding.finish.unavailable", view);
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
        Assert.Contains("private static bool OnboardingDurabilityAvailable => false;", app);
        Assert.Contains("if (!OnboardingDurabilityAvailable)", app);
        Assert.Contains("_onboardingCreatingStarter, OnboardingDurabilityAvailable", app);
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
            "NativeOnboarding_StarterFailed", "Coach_Hint_Unknown"
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
