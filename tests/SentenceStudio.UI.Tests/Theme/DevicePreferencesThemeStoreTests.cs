using FluentAssertions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services;
using SentenceStudio.Services.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// MAUI semantics: the appearance belongs to the installation, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// On a device there is no isolation problem to solve — one install, one sandbox, one preference
/// store — so <c>ThemeService</c> stays a singleton there. What these tests pin is the other half
/// of the product decision: the value goes to the <i>device's</i> preference store and nowhere near
/// the account, so the same learner's phone and desktop stay independent.
/// </para>
/// </remarks>
public class DevicePreferencesThemeStoreTests
{
    [Fact]
    public void The_maui_registration_keeps_theme_state_a_singleton_because_the_process_is_the_device()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDeviceThemePresentation();

        services.Single(d => d.ServiceType == typeof(ThemeService))
            .Lifetime.Should().Be(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(IThemePreferenceStore))
            .Lifetime.Should().Be(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);
    }

    [Fact]
    public void Reads_and_writes_the_device_preference_store_synchronously()
    {
        var preferences = new InMemoryPreferences();
        var store = new DevicePreferencesThemeStore(preferences);

        store.TryLoad(out _).Should().BeFalse("nothing has been chosen on this device yet");

        var service = new ThemeService(store);
        service.Current.Should().Be(AppearanceSelection.Default);
    }

    [Fact]
    public async Task A_choice_persists_across_a_restart_of_the_same_install()
    {
        var preferences = new InMemoryPreferences();

        var firstLaunch = new ThemeService(new DevicePreferencesThemeStore(preferences));
        await firstLaunch.SetThemeAsync("forest");
        await firstLaunch.SetModeAsync(ThemeMode.Light);
        await firstLaunch.SetFontScaleAsync(1.3);

        // Same preference store, new process.
        var secondLaunch = new ThemeService(new DevicePreferencesThemeStore(preferences));

        secondLaunch.Current.Should().Be(new AppearanceSelection("forest", ThemeMode.Light, 1.3));
    }

    [Fact]
    public async Task Two_devices_keep_independent_choices()
    {
        // Two preference stores stands in for two installs. There is no shared substrate between
        // them — which is the point: appearance is never account-wide.
        var phone = new ThemeService(new DevicePreferencesThemeStore(new InMemoryPreferences()));
        var desktop = new ThemeService(new DevicePreferencesThemeStore(new InMemoryPreferences()));

        await phone.SetThemeAsync("vapor");
        await desktop.SetThemeAsync("flatly");

        phone.CurrentTheme.Should().Be("vapor");
        desktop.CurrentTheme.Should().Be("flatly");
    }

    [Fact]
    public async Task The_stored_value_is_the_same_bounded_token_the_web_cookie_uses()
    {
        var preferences = new InMemoryPreferences();
        var store = new DevicePreferencesThemeStore(preferences);

        await store.SaveAsync(new AppearanceSelection("ocean", ThemeMode.Light, 1.05));

        preferences.Get(DevicePreferencesThemeStore.AppearanceKey, string.Empty)
            .Should().Be("v1.ocean.light.105");
    }

    [Fact]
    public void An_unparseable_stored_token_falls_back_to_the_default_rather_than_crashing_startup()
    {
        var preferences = new InMemoryPreferences();
        preferences.Set(DevicePreferencesThemeStore.AppearanceKey, "not-a-token");

        new ThemeService(new DevicePreferencesThemeStore(preferences))
            .Current.Should().Be(AppearanceSelection.Default);
    }

    // -------------------------------------------------------------------------------------------
    // Adoption of what shipped builds already wrote
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_learners_existing_choice_from_a_shipped_build_is_adopted_not_reset()
    {
        var preferences = new InMemoryPreferences();
        preferences.Set(DevicePreferencesThemeStore.LegacyThemeKey, "sunset");
        preferences.Set(DevicePreferencesThemeStore.LegacyModeKey, "light");
        preferences.Set(DevicePreferencesThemeStore.LegacyFontScaleKey, 1.2);

        new ThemeService(new DevicePreferencesThemeStore(preferences))
            .Current.Should().Be(new AppearanceSelection("sunset", ThemeMode.Light, 1.2));
    }

    [Fact]
    public void A_legacy_value_that_no_longer_validates_degrades_to_the_default_for_that_field_only()
    {
        var preferences = new InMemoryPreferences();
        preferences.Set(DevicePreferencesThemeStore.LegacyThemeKey, "a-theme-we-removed");
        preferences.Set(DevicePreferencesThemeStore.LegacyModeKey, "light");
        preferences.Set(DevicePreferencesThemeStore.LegacyFontScaleKey, 99.0);

        var current = new ThemeService(new DevicePreferencesThemeStore(preferences)).Current;

        current.ThemeId.Should().Be(ThemeCatalog.DefaultThemeId, "the stored theme no longer exists");
        current.Mode.Should().Be(ThemeMode.Light, "the mode was still valid and is kept");
        current.FontScale.Should().Be(AppearanceSelection.DefaultFontScale);
    }

    [Fact]
    public async Task The_new_token_wins_over_stale_legacy_keys()
    {
        var preferences = new InMemoryPreferences();
        preferences.Set(DevicePreferencesThemeStore.LegacyThemeKey, "sunset");

        var store = new DevicePreferencesThemeStore(preferences);
        await store.SaveAsync(new AppearanceSelection("slate", ThemeMode.Dark, 1.0));

        new ThemeService(new DevicePreferencesThemeStore(preferences))
            .CurrentTheme.Should().Be("slate");
    }

    /// <summary>A preference store with a dictionary behind it, matching MAUI's typed contract.</summary>
    private sealed class InMemoryPreferences : IPreferencesService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);

        public void Clear() => _values.Clear();
    }
}
