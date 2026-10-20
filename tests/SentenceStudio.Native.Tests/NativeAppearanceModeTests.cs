using System.Globalization;
using System.Resources;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services.Theme;

namespace SentenceStudio.Native.Tests;

public sealed class NativeAppearanceModeTests
{
    [Fact]
    public void ExistingModeValuesAndTokensStayStable_WhenSystemIsAdded()
    {
        Assert.Equal(0, (int)ThemeMode.Light);
        Assert.Equal(1, (int)ThemeMode.Dark);
        Assert.Equal(2, (int)ThemeMode.System);
        Assert.Equal(ThemeMode.Dark, AppearanceSelection.Default.Mode);
        Assert.Equal("v1.seoul-pop.dark.100", AppearanceSelection.Default.ToToken());
        Assert.Equal("v1.ocean.light.125",
            new AppearanceSelection("ocean", ThemeMode.Light, 1.25).ToToken());
        Assert.True(ThemeModeExtensions.TryParse("SYSTEM", out var parsed));
        Assert.Equal(ThemeMode.System, parsed);
        Assert.False(ThemeModeExtensions.TryParse("auto", out _));
    }

    [Theory]
    [InlineData("en", "System")]
    [InlineData("ko", "시스템")]
    public void SystemModeHasAnEnglishAndKoreanSettingsLabel(
        string language, string expected)
    {
        var resources = new ResourceManager(
            "SentenceStudio.Resources.Strings.AppResources",
            typeof(LocalizationManager).Assembly);

        Assert.Equal(expected, resources.GetString(
            "Settings_SystemMode", CultureInfo.GetCultureInfo(language)));
    }

    [Theory]
    [InlineData(ThemeMode.System, false, ThemeMode.Light)]
    [InlineData(ThemeMode.System, true, ThemeMode.Dark)]
    [InlineData(ThemeMode.Light, false, ThemeMode.Light)]
    [InlineData(ThemeMode.Light, true, ThemeMode.Light)]
    [InlineData(ThemeMode.Dark, false, ThemeMode.Dark)]
    [InlineData(ThemeMode.Dark, true, ThemeMode.Dark)]
    public void EffectivePaletteTracksTheOsOnlyWhenSystemSelected(
        ThemeMode selected, bool systemIsDark, ThemeMode effective)
    {
        var choice = new AppearanceSelection("ocean", selected, 1.25);

        Assert.Equal(effective, selected.ResolveEffective(systemIsDark));
        Assert.Equal(choice.Theme.PaletteFor(effective),
            choice.PaletteFor(systemIsDark));
    }

    [Fact]
    public async Task ExistingDevicePreferenceFormatRoundTripsSystemAndKeepsLegacyLight()
    {
        var preferences = new NativeSliceFixture.ScratchPreferences();
        preferences.Set(DevicePreferencesThemeStore.LegacyModeKey, "light");
        var store = new DevicePreferencesThemeStore(preferences);

        Assert.True(store.TryLoad(out var legacy));
        Assert.Equal(ThemeMode.Light, legacy.Mode);

        var saved = legacy.WithMode(ThemeMode.System);
        await store.SaveAsync(saved);
        Assert.Equal("v1.seoul-pop.system.100",
            preferences.Get(DevicePreferencesThemeStore.AppearanceKey, ""));

        var restarted = new DevicePreferencesThemeStore(preferences);
        Assert.True(restarted.TryLoad(out var restored));
        Assert.Equal(saved, restored);
    }
}
