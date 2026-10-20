using System.Runtime.CompilerServices;
using SentenceStudio.Native.iOS.Features.Settings;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeAppearanceSettingsContractTests
{
    [Fact]
    public void AppearanceAndQuizControls_UsePersistedNormalSettingsNotDebugFixtures()
    {
        var app = Read("App/ApplicationController.cs");
        var scene = Read("App/ApplicationScene.cs");
        var appearance = Read("Features/Settings/AppearanceSettingsView.cs");
        var quiz = Read("Features/Settings/QuizSettingsView.cs");
        var owner = Read("Features/Settings/SettingsCoordinator.cs");
        var dashboard = Read("Features/Dashboard/DashboardView.cs");

        Assert.Contains("SourceUiAppearance.Save(_preferences, selection)", owner);
        Assert.Contains("QuizSettingsDraft.FromState(", owner);
        Assert.Contains("GetRequiredService<NativeQuizSettings>()", app);
        Assert.Contains("_quiz.Save(owner, draft.ToState(), _stamp)", owner);
        Assert.Contains("LoadSettingsVoicesAsync(", app);
        Assert.Contains("allowVoiceDiscovery: !_preview", app);
        Assert.Contains("SettingsOwnerAccess.Resolve(", app);
        Assert.Contains("NativePreviewFixture.FromAssembly()?.Owner", app);
        Assert.Contains("#if DEBUG", owner);
        Assert.Contains("if (debugBuild && bundleId == FixtureBundleId)", owner);
        Assert.Contains("_scope.ScopeChanged += OnScopeChanged", owner);
        Assert.Contains("_scope.ScopeChanged -= OnScopeChanged", owner);
        Assert.Contains("SettingsView.OwnerUnavailable(style, Text)", app);
        Assert.Contains("return SettingsView.OwnerUnavailable", app);
        Assert.Contains("Views.Text(text(\"NativeSettings_OwnerUnavailable\"))",
            Read("Features/Settings/SettingsView.cs"));
        Assert.Contains("result = _allowVoiceDiscovery", owner);
        Assert.Contains("Views.Button(text(\"Settings_Title\"), actions.OpenSettings)",
            dashboard);
        Assert.Contains("_dashboardStack.Push(\"settings\", _settingsSlot)", scene);
        Assert.Contains("_root.PreferredColorScheme = PreferredSchemeFor(selectedAppearance.Mode)", scene);
        Assert.Contains("SourceUiStyle.RefreshTypography(", scene);
        Assert.Contains("_settingsSlot = Views.VStack(_app.RenderSettings(_style))",
            scene);
        Assert.Contains("WithPickerStyle(SwiftPickerStyle.Segmented)", appearance);
        Assert.Contains("ThemeCatalog.All.Select", appearance);
        Assert.Contains("Views.Slider(", appearance);
        Assert.Contains("Views.Slider(", quiz);
        Assert.Contains("Views.Toggle(", quiz);
        Assert.Contains("native.settings.quiz.voice-language", quiz);
        Assert.Contains("native.settings.quiz.voice", quiz);
        Assert.Contains("NativeSettings_CurrentVoice", quiz);
        Assert.Contains("NativeSettings_VoicesRequireSignIn", owner);
        Assert.Contains("save.IsEnabled = !voiceLoading;", quiz);
        Assert.Contains("!changed && !_voiceLoading && _draft is not null", owner);
        Assert.DoesNotContain("Local preview - not authenticated", app);
        Assert.DoesNotContain("Local test content - authenticated", app);
    }

    [Fact]
    public void DynamicThemeSwitch_UpdatesCurrentSwiftUISceneWithoutReplacingNavigationStacks()
    {
        var scene = Read("App/ApplicationScene.cs");
        var appearanceUpdate = scene[scene.IndexOf(
            "var previousAppearance = _style.Appearance.Selection;",
            StringComparison.Ordinal)..scene.IndexOf(
            "var target = _app.Destination switch", StringComparison.Ordinal)];

        Assert.Contains("new SourceUiStyle(_app.Appearance, _style.ViewportWidth)",
            appearanceUpdate);
        Assert.Contains("_style.Page(_root);", appearanceUpdate);
        Assert.Contains("_root.PreferredColorScheme = PreferredSchemeFor(selectedAppearance.Mode)",
            appearanceUpdate);
        Assert.Contains("SourceUiStyle.RefreshTypography(", appearanceUpdate);
        Assert.DoesNotContain("Install(", appearanceUpdate);
        Assert.DoesNotContain("InstallTabs(", appearanceUpdate);
        Assert.Contains("UpdateResources(themeChanged || modeChanged);", scene);
        Assert.Contains("UpdateResourceForm(themeChanged || modeChanged);", scene);
        Assert.Contains("_app.IsQuizOpen || themeChanged || modeChanged",
            scene);
        Assert.Contains("UpdateSettings(themeChanged, modeChanged, fontScaleChanged);",
            scene);
    }

    [Fact]
    public void SettingsFeedback_KeepsTheMountedScrollAndContentHost()
    {
        var scene = Read("App/ApplicationScene.cs");
        var update = scene[scene.IndexOf("private void UpdateSettings(",
            StringComparison.Ordinal)..scene.IndexOf(
            "private void StartQuizProgressIfGraded()", StringComparison.Ordinal)];

        Assert.Contains("!themeChanged && !modeChanged", update);
        Assert.Contains("Find(scroll, \"native.settings.scroll.content\")", update);
        Assert.Contains("next.RemoveAt(0)", update);
        Assert.Contains("ReplaceContainerChildren(currentContent, content)", update);
        Assert.Contains("next.Dispose()", update);
    }

    [Fact]
    public void SystemIsNoPreferredColorScheme_WhileExplicitChoicesOverrideTheEnvironment()
    {
        var scene = Read("App/ApplicationScene.cs");
        Assert.Contains("ThemeMode.System => SwiftColorScheme.System", scene);
        Assert.Contains("ThemeMode.Light => SwiftColorScheme.Light", scene);
        Assert.Contains("ThemeMode.Dark => SwiftColorScheme.Dark", scene);
        Assert.Contains(".WithPreferredColorScheme(PreferredSchemeFor(app.Appearance.Selection.Mode))",
            scene);
        Assert.Contains("_root.PreferredColorScheme = PreferredSchemeFor(selectedAppearance.Mode);",
            scene);
        var settings = Read("Features/Settings/AppearanceSettingsView.cs");
        Assert.Contains("new SwiftPickerOption(\"system\", text(\"Settings_SystemMode\"))",
            settings);
        Assert.Contains("selection.Mode.ToToken()", settings);
        Assert.Contains("ThemeModeExtensions.TryParse(value, out var selected)",
            settings);
        Assert.Contains("Views.Circle(24, theme.Light.Primary.Hex, theme.Dark.Primary.Hex)",
            settings);
    }

    [Fact]
    public void SliderAccessibilityLabels_UseFormattedCurrentValuesNotResourcePlaceholders()
    {
        var appearance = Read("Features/Settings/AppearanceSettingsView.cs");
        var quiz = Read("Features/Settings/QuizSettingsView.cs");
        Assert.Contains("text(\"Settings_TextSizeLabel\"),\n                    (int)Math.Round(selection.FontScale * 100)", appearance);
        Assert.Contains("scaleSlider.AccessibilityLabel = string.Format(",
            appearance);
        Assert.Contains("Views.Slider(\n                DurationTitle(quiz.AutoAdvanceSeconds, text)",
            quiz);
        Assert.Contains("durationSlider.AccessibilityLabel = DurationTitle(seconds, text)",
            quiz);
        Assert.DoesNotContain("Views.Slider(text(\"Settings_TextSizeLabel\")",
            appearance);
    }

    [Theory]
    [InlineData(0.85, 0.85)]
    [InlineData(1.024, 1.0)]
    [InlineData(1.026, 1.05)]
    [InlineData(1.501, 1.5)]
    public void SettingsSlider_SnapsToSourceFivePercentSteps(
        double input, double expected) =>
        Assert.Equal(expected,
            SettingsInput.SnapFontScale(input), 3);

    [Theory]
    [InlineData(0.2, 1.0)]
    [InlineData(1.24, 1.0)]
    [InlineData(1.26, 1.5)]
    [InlineData(10.8, 10.0)]
    public void QuizDuration_SnapsToSourceHalfSecondSteps(
        double input, double expected) =>
        Assert.Equal(expected, QuizSettingsDraft.SnapDuration(input), 3);

    [Fact]
    public void InvalidAllOffPromptPreferences_FallBackToTextWithoutRemovingAudioPhotoChoice()
    {
        var allOff = new QuizSettingsDraft(
            "Mixed", true, true, false, false, false, 2,
            "Korean", "voice-id", []);
        var valid = allOff.WithValidModalities();

        Assert.True(valid.TextPrompt);
        Assert.False(valid.AudioPrompt);
        Assert.False(valid.PhotoPrompt);
        Assert.Equal("Mixed", valid.Direction);
        Assert.Equal(2, valid.AutoAdvanceSeconds);
        Assert.Equal("Korean", valid.VoiceLanguage);
        Assert.Equal("voice-id", valid.ToState().VoiceId);
    }

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(
            Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(
                    CallerFilePath())!, "..", "..", "src",
                "SentenceStudio.Native.iOS")),
            file));

    private static string CallerFilePath(
        [CallerFilePath] string path = "") => path;
}
