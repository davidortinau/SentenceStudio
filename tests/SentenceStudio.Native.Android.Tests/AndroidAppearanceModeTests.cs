using System.Reflection;
using SentenceStudio.Contracts.Theme;

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
        Assert.Contains("new DevicePreferencesThemeStore(preferences).SaveAsync(selection)", more);
        Assert.Contains("AndroidFontScale.Apply(activity, saved.FontScale)", more);
        Assert.Contains("new AndroidPreferences(activity, preview)", shell);
        Assert.Contains("if (HandleMoreBack())", shell);
        Assert.Contains("IsMoreScreenOpen\n            || AndroidRoutePolicy.HandlesBack(Destination)", shell);
        Assert.Contains("when IsMoreScreenOpen => RenderMoreScreen(palette)", compose);
        Assert.DoesNotContain("Android.Widget", settings);
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
    public void EveryThemeKeepsTextAndMarksLegibleOnItsPage(string themeId, bool dark)
    {
        var tokens = AndroidThemeTokensCatalog.For(themeId, dark);
        var page = SrgbColor.Parse(tokens.Background);
        var card = SrgbColor.Parse(tokens.Card);
        Assert.True(SrgbColor.Parse(tokens.Text).ContrastRatio(page) >= 4.5, "text on page");
        Assert.True(SrgbColor.Parse(tokens.Text).ContrastRatio(card) >= 4.5, "text on card");
        Assert.True(SrgbColor.Parse(tokens.SecondaryText).ContrastRatio(card) >= 4.5,
            "secondary text on card");
        Assert.True(SrgbColor.Parse(tokens.Primary).ContrastRatio(page) >= 3.0, "primary on page");
        Assert.True(SrgbColor.Parse(tokens.OnPrimary).ContrastRatio(
            SrgbColor.Parse(tokens.Primary)) >= 4.5, "label on primary");
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidAppearanceModeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
