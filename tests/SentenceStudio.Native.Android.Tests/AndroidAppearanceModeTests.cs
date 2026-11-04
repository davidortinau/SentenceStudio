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

        Assert.Contains("native.dashboard.menu.settings", visual);
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
        Assert.Contains("IsMoreScreenOpen\n            || AndroidRoutePolicy.HandlesBack(Destination)", shell);
        Assert.Contains("when IsMoreScreenOpen => RenderMoreScreen(palette)", compose);
        Assert.DoesNotContain("Android.Widget", settings);
    }

    [Fact]
    public void TextSizeAppliesLiveAndSavesOnceTheDragSettlesOffTheUiThread()
    {
        var settings = Source("AndroidSliceShell.Appearance.cs");
        var more = Source("AndroidSliceShell.More.cs");

        // Each step applies live; the facade Slider has no onValueChangeFinished,
        // so the save waits until no further step arrives.
        Assert.Contains("raw => PreviewTextSize(SettingsInput.SnapFontScale(raw))", settings);
        Assert.Contains("ChangeAppearance(CurrentAppearance.WithFontScale(fontScale), settle: true)",
            more);
        Assert.Contains("TimeSpan.FromMilliseconds(500)", more);
        Assert.Contains("await Task.Delay(TextSizeSettleDelay, settle)", more);
        // Theme and mode taps save at once; every save runs on the thread pool, in order.
        Assert.Contains("ChangeAppearance(selection, settle: false)", more);
        Assert.Contains("var save = Task.Run(async () =>", more);
        Assert.Contains("await previous.ConfigureAwait(false);", more);
        Assert.DoesNotContain("GetAwaiter().GetResult()", more);
        // A failed save restores the stored appearance (the coordinator shows SaveFailed).
        Assert.Contains("_appearance = null;\n            AndroidFontScale.Apply(activity, CurrentAppearance.FontScale);",
            more);
        // Closing Settings saves a pending text size before the coordinator is disposed.
        var release = more[more.IndexOf("private void ReleaseMoreCoordinators()",
            StringComparison.Ordinal)..];
        Assert.True(release.IndexOf("FlushAppearanceSave(settings)", StringComparison.Ordinal)
            < release.IndexOf("settings.Dispose()", StringComparison.Ordinal));
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

    private static string Source(string name)
    {
        var root = typeof(AndroidAppearanceModeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
