using System.Reflection;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidAppearanceModeTests
{
    [Fact]
    public void SettingsUsesComposeControlsAndPersistsOnlyASelectedMode()
    {
        var settings = Source("AndroidSliceShell.Appearance.cs");
        var visual = Source("AndroidSliceShell.Visual.cs");
        var shell = Source("AndroidSliceShell.cs");
        var compose = Source("AndroidSliceShell.Compose.cs");

        Assert.Contains("native.dashboard.menu.settings", visual);
        Assert.Contains("OpenAppearanceSettings", visual);
        Assert.Contains("new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark }",
            settings);
        Assert.Contains("new OutlinedButton(", settings);
        Assert.Contains("semantics.Selected(selected)", settings);
        Assert.Contains("native.settings.mode.{mode.ToToken()}", settings);
        Assert.Contains("new DevicePreferencesThemeStore(", shell);
        Assert.Contains("_appearanceStore.SaveAsync(next, cancellationToken)", shell);
        Assert.Contains("CurrentAppearance.WithMode(mode)", shell);
        Assert.Contains("new AndroidPreferences(activity, preview)", shell);
        Assert.Contains("if (_appearanceSettingsOpen)", shell);
        Assert.Contains("_appearanceSettingsOpen\n            || AndroidRoutePolicy.HandlesBack(Destination)", shell);
        Assert.Contains("RenderAppearanceSettings(palette)", compose);
        Assert.DoesNotContain("Android.Widget", settings);
    }

    [Fact]
    public void EffectivePaletteRecomposesFromActivityUiModeOnlyForSystem()
    {
        var activity = Source("MainActivity.cs");
        var compose = Source("AndroidSliceShell.Compose.cs");

        Assert.Contains("ConfigurationChanges = ConfigChanges.UiMode", activity);
        Assert.Contains("_shell?.RefreshForUiMode()", activity);
        Assert.Contains("_ = _revision.Value;", compose);
        Assert.Contains("MaterialTheme.IsSystemInDarkTheme(composer)", compose);
        Assert.Contains("CurrentAppearance.Mode.ResolveEffective(systemIsDark)", compose);
        Assert.Contains("AndroidVisualTheme.ColorScheme(dark)", compose);
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidAppearanceModeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
