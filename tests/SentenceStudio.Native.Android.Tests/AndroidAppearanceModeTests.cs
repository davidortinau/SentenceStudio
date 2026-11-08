using System.Reflection;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>Android Settings appearance: theme, mode and text size (AF-Q14).</summary>
public sealed class AndroidAppearanceModeTests
{
    [Fact]
    public void SettingsUsesComposeControlsAndTheSharedOwnerGatedAppearanceSave()
    {
        var settings = Source("AndroidSliceShell.Appearance.cs");
        var more = Source("AndroidSliceShell.More.cs");
        var visual = Source("AndroidSliceShell.Visual.cs");
        var shell = Source("AndroidSliceShell.cs");
        var compose = Source("AndroidSliceShell.Compose.cs");

        Assert.Contains("\"native.tab.settings\"", Source("AndroidSliceShell.Tabs.Compose.cs"));
        Assert.Contains("ThemeCatalog.All", settings);
        Assert.Contains("native.settings.theme.{theme.Id}", settings);
        Assert.Contains("semantics.Selected(chosen)", settings);
        Assert.Contains("MoreSegments(\"native.settings.mode\"", settings);
        Assert.Contains("CurrentAppearance.WithMode(mode)", settings);
        Assert.Contains("CurrentAppearance.WithTheme(theme.Id)", settings);
        Assert.Contains("SettingsInput.SnapFontScale(raw)", settings);
        Assert.Contains("steps: 12", settings);
        // The shared SettingsCoordinator gates the write on the verified owner.
        Assert.Contains("settings.SaveAppearance(selection, SaveDeviceAppearance)", more);
        // The same AppAppearance token DevicePreferencesThemeStore and iOS write.
        Assert.Contains(
            "preferences.Set(DevicePreferencesThemeStore.AppearanceKey, selection.ToToken())", more);
        Assert.Contains("AndroidFontScale.Apply(activity, selection.FontScale)", more);
        Assert.Contains("new AndroidPreferences(activity, preview)", shell);
        Assert.Contains("if (HandleMoreBack())", shell);
        Assert.Contains("IsMoreScreenOpen\n            || HandlesTabRootBack\n            || AndroidRoutePolicy.HandlesBack(Destination)", shell);
        Assert.Contains("when IsMoreScreenOpen => WithMoreToast(composer, RenderMoreScreen(palette))", compose);
        Assert.Contains("ShellTab.Settings => RenderSettings(palette)", compose);
        Assert.DoesNotContain("Android.Widget", settings);
    }

    [Fact]
    public void TextSizePreviewsWhileDraggingAndAppliesAndSavesOnRelease()
    {
        var settings = Source("AndroidSliceShell.Appearance.cs");
        var more = Source("AndroidSliceShell.More.cs");

        // Captain 2026-11-04 (decision A): a font-scale change resets Compose pointer
        // input, so a move only previews; onValueChangeFinished (release, key up,
        // accessibility set-progress) applies the app-wide scale and saves it once.
        Assert.Contains("var shownScale = _textSizePreview ?? selection.FontScale;", settings);
        // Disabled mid-drag (busy), the drag ends without a release: drop the unapplied preview.
        Assert.Contains("if (Busy.Value)\n            _textSizePreview = null;\n        var shownScale", settings);
        Assert.Contains("(int)Math.Round(shownScale * 100)", settings);
        Assert.Contains("new AndroidReleaseSlider((float)shownScale,", settings);
        Assert.Contains("raw => PreviewTextSize(SettingsInput.SnapFontScale(raw)),\n"
            + "            CommitTextSize,", settings);
        Assert.DoesNotContain("new Slider((float)selection.FontScale", settings);
        // The tag and label stay on the slider's own node, as with the facade Slider.
        Assert.Contains("testTag: \"native.settings.text-size\",\n"
            + "            contentDescription: sizeLabel));", settings);

        var preview = Method(more, "private void PreviewTextSize(double fontScale)");
        Assert.Contains("_textSizePreview = fontScale;", preview);
        Assert.Contains("RenderMoreNow();", preview);
        Assert.DoesNotContain("AndroidFontScale", preview);
        Assert.DoesNotContain("SetAppearance", preview);
        Assert.DoesNotContain("QueueAppearanceSave", preview);
        var commit = Method(more, "private void CommitTextSize()");
        Assert.Contains("_textSizePreview = null;", commit);
        Assert.Contains("SetAppearance(CurrentAppearance.WithFontScale(fontScale));", commit);
        // No idle-timer save any more: release is the end of the gesture.
        Assert.DoesNotContain("TextSizeSettleDelay", more);
        Assert.DoesNotContain("Task.Delay", more);

        // Theme, mode and a released text size save at once, on the thread pool, in order.
        var set = Method(more, "private void SetAppearance(AppearanceSelection selection)");
        Assert.Contains("AndroidFontScale.Apply(activity, selection.FontScale);", set);
        Assert.Contains("QueueAppearanceSave(settings, selection, ++_appearanceRequest);", set);
        Assert.Contains("var save = Task.Run(async () =>", more);
        Assert.Contains("await previous.ConfigureAwait(false);", more);
        Assert.DoesNotContain("GetAwaiter().GetResult()", more);
        // A failed save restores the stored appearance (the coordinator shows SaveFailed).
        Assert.Contains("_appearance = null;\n            AndroidFontScale.Apply(activity, CurrentAppearance.FontScale);",
            more);
        // Closing Settings drops an unreleased preview and lets queued saves finish
        // before the coordinator is disposed.
        var release = Method(more, "private void ReleaseSettings()");
        Assert.Contains("_textSizePreview = null;", release);
        Assert.True(release.IndexOf("var saves = _appearanceSaves;", StringComparison.Ordinal)
            < release.IndexOf("settings.Dispose()", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseSliderIsTheMaterialSliderWithOnlyOnValueChangeFinishedAdded()
    {
        var slider = Source("AndroidReleaseSlider.cs");

        // The same bound overload the facade Slider calls (kept by Release/R8 for it),
        // with onValueChangeFinished supplied instead of null.
        Assert.Contains("SliderKt.Slider(", slider);
        Assert.Contains("onValueChangeFinished: finished,", slider);
        Assert.Contains("p7: steps,", slider);
        Assert.Contains("valueRange: range,", slider);
        Assert.Contains("_changed1: KotlinDefaults);", slider);
        // Defaults only for colors, interactionSource, thumb and track; value, onValueChange,
        // modifier, enabled, onValueChangeFinished, steps and valueRange are passed.
        Assert.Contains("KotlinDefaults = (1 << 5) | (1 << 6) | (1 << 8) | (1 << 9);", slider);
        // The slider's own modifier: test tag, then a fresh content-description lambda.
        Assert.Contains("TestTagKt.TestTag(EmptyModifier(), testTag)", slider);
        Assert.Contains("new ContentDescriptionCallback(contentDescription)", slider);
        Assert.Contains("SemanticsPropertiesKt.SetContentDescription(", slider);
        // R8: the facade keeps only semantics$default, so the app keeps the overload it calls.
        var r8 = Source("proguard-compose-jni.cfg");
        Assert.Contains("public static androidx.compose.ui.Modifier semantics("
            + "androidx.compose.ui.Modifier, boolean, kotlin.jvm.functions.Function1);", r8);
        Assert.Contains("composer.Remember(() => new FloatCallback())", slider);
        Assert.Contains("composer.Remember(() => new UnitCallback())", slider);
        Assert.DoesNotContain("Android.Widget", slider);
        Assert.DoesNotContain("Android.Views", slider);
    }

    [Fact]
    public void EffectivePaletteRecomposesFromActivityUiModeAndTheSelectedTheme()
    {
        var activity = Source("MainActivity.cs");
        var compose = Source("AndroidSliceShell.Compose.cs");
        var shell = Source("AndroidSliceShell.cs");

        Assert.Contains("ConfigurationChanges = ConfigChanges.UiMode", activity);
        Assert.Contains("_shell?.RefreshForUiMode()", activity);
        Assert.Contains("_ = _revision.Value;", compose);
        Assert.Contains("MaterialTheme.IsSystemInDarkTheme(composer)", compose);
        Assert.Contains("CurrentAppearance.Mode.ResolveEffective(systemIsDark)", compose);
        Assert.Contains("AndroidVisualTheme.ColorScheme(dark, themeId)", compose);
        Assert.Contains("AndroidVisualTheme.For(dark, themeId)", compose);
        // Text size applies at launch and is reapplied after any configuration change.
        Assert.Contains("ApplyAppearanceFontScale();\n        Execute(InitializeNormalAsync);", shell);
        Assert.Contains("ApplyAppearanceFontScale();\n        _revision.Value", shell);
    }

    [Fact]
    public void TextSizeScalesOnlySpThroughTheActivityConfiguration()
    {
        var scale = Source("AndroidFontScale.cs");
        Assert.Contains("FontScale = target", scale);
        Assert.Contains("DispatchConfigurationChanged(updated)", scale);
        Assert.Contains("SystemScale * appScale", scale);
        Assert.DoesNotContain("Android.Widget", scale);
        Assert.DoesNotContain("Recreate()", scale);
    }

    [Fact]
    public void SeoulPopKeepsThePinnedAndroidRoles()
    {
        var light = AndroidThemeTokensCatalog.For("seoul-pop", dark: false);
        var dark = AndroidThemeTokensCatalog.For("seoul-pop", dark: true);
        Assert.Equal(("#1E4DFF", "#FFFFFF", "#F7F8FF", "#0B0D12", "#EEF1FF", "#D6DAF0"),
            (light.Primary, light.OnPrimary, light.Background, light.Text, light.Card, light.Border));
        Assert.Equal(("#6B8CFF", "#081022", "#060913", "#F8FAFF", "#121B33", "#263457"),
            (dark.Primary, dark.OnPrimary, dark.Background, dark.Text, dark.Card, dark.Border));
        Assert.Equal(dark, AndroidThemeTokensCatalog.For("not-a-theme", dark: true));
    }

    public static TheoryData<string, bool> Themes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var theme in ThemeCatalog.All)
        {
            data.Add(theme.Id, false);
            data.Add(theme.Id, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryThemeUsesTheSharedExactWebColours(string themeId, bool dark)
    {
        // Captain 2026-11-04 (decision 8b): Android paints the web app's exact colours,
        // from the same shared table iOS reads, for every theme and mode. The web's own
        // low-contrast pairs (for example Vapor light cards) are kept, as on iOS.
        var tokens = AndroidThemeTokensCatalog.For(themeId, dark);
        var web = NativeThemeColorTable.For(themeId, dark);
        Assert.Equal(
            (web.Primary, web.OnPrimary, web.Accent, web.Body, web.Text, web.SecondaryText,
                web.TertiaryText, web.SecondarySurface, web.CardSurface,
                web.ElevatedSurface, web.Border, web.Secondary, web.IsDarkSurface),
            (tokens.Primary, tokens.OnPrimary, tokens.Accent, tokens.Background,
                tokens.Text, tokens.SecondaryText, tokens.TertiaryText,
                tokens.SecondarySurface, tokens.Card, tokens.Elevated, tokens.Border,
                tokens.Secondary, tokens.DarkSurface));
        var page = SrgbColor.Parse(tokens.Background);
        Assert.True(SrgbColor.Parse(tokens.Text).ContrastRatio(page) >= 4.5, "text on page");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void SelectedSegmentsAndTabIndicatorUseTheThemeNotMaterialPurple(
        string themeId, bool dark)
    {
        var tokens = AndroidThemeTokensCatalog.For(themeId, dark);
        Assert.Equal(AndroidThemeTokensCatalog.Blend(tokens.Primary, tokens.Background,
            AndroidThemeTokensCatalog.SelectedContainerOpacity), tokens.SelectedContainer);
        // Material 3 baseline secondaryContainer (light #E8DEF8, dark #4A4458).
        Assert.DoesNotContain(tokens.SelectedContainer, new[] { "#E8DEF8", "#4A4458" });
        Assert.True(SrgbColor.Parse(tokens.Text).ContrastRatio(
            SrgbColor.Parse(tokens.SelectedContainer)) >= 4.5, "segment label on selection");

        var theme = Source("AndroidVisualTheme.cs");
        foreach (var role in new[]
        {
            "secondaryContainer: color.SelectedContainer", "onSecondaryContainer: color.Text",
            "primaryContainer: color.SelectedContainer", "surfaceContainer: color.Card",
            "surfaceContainerHigh: color.Elevated", "surfaceContainerHighest: color.Elevated",
            "surfaceContainerLow: color.Card", "surfaceContainerLowest: color.Background",
        })
            Assert.Equal(2, Occurrences(theme, role));
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[start..(index + 1)];
        }
        throw new InvalidOperationException(signature);
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidAppearanceModeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
