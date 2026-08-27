using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Services.Theme;

/// <summary>
/// Device-scoped appearance storage for the MAUI heads, over the platform preference store.
/// </summary>
/// <remarks>
/// <para>
/// On MAUI, <see cref="IPreferencesService"/> already <i>is</i> per-device: it maps to
/// <c>NSUserDefaults</c> / <c>SharedPreferences</c> inside the app sandbox, so there is nothing to
/// isolate — one install, one value. That is exactly the semantic the product wants, and it is why
/// the MAUI store stays a singleton while the web store must be scoped.
/// </para>
/// <para>
/// The value is stored as the same bounded token the web cookie uses, so a token that round-trips
/// on one host round-trips on the other and there is one parser to keep honest. The three legacy
/// keys are still read once, on first load, so a learner who already picked a theme in a shipped
/// build does not get reset to the default by this refactor.
/// </para>
/// </remarks>
public sealed class DevicePreferencesThemeStore : IThemePreferenceStore
{
    /// <summary>
    /// Preference key holding the bounded appearance token.
    /// </summary>
    /// <remarks>
    /// Public because a preference key is a compatibility surface, not an implementation detail: it
    /// names a slot that already exists on every learner's device, and renaming it silently resets
    /// their theme. Tests assert against it for that reason.
    /// </remarks>
    public const string AppearanceKey = "AppAppearance";

    /// <summary>Pre-refactor theme key. Read-only, and only when <see cref="AppearanceKey"/> is absent.</summary>
    public const string LegacyThemeKey = "AppTheme";

    /// <summary>Pre-refactor mode key. Read-only, and only when <see cref="AppearanceKey"/> is absent.</summary>
    public const string LegacyModeKey = "AppThemeMode";

    /// <summary>Pre-refactor text-size key. Read-only, and only when <see cref="AppearanceKey"/> is absent.</summary>
    public const string LegacyFontScaleKey = "AppFontScale";

    private readonly IPreferencesService _preferences;
    private readonly ILogger<DevicePreferencesThemeStore>? _logger;

    public DevicePreferencesThemeStore(
        IPreferencesService preferences,
        ILogger<DevicePreferencesThemeStore>? logger = null)
    {
        _preferences = preferences;
        _logger = logger;
    }

    public bool TryLoad(out AppearanceSelection selection)
    {
        var token = _preferences.Get(AppearanceKey, string.Empty);
        if (AppearanceSelection.TryParse(token, out selection))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(token))
        {
            _logger?.LogWarning(
                "Discarding an unparseable appearance token from device preferences; falling back to the default.");
        }

        return TryLoadLegacy(out selection);
    }

    public ValueTask<AppearanceSelection?> LoadAsync(CancellationToken cancellationToken = default) =>
        new(TryLoad(out var selection) ? selection : null);

    public ValueTask SaveAsync(AppearanceSelection selection, CancellationToken cancellationToken = default)
    {
        _preferences.Set(AppearanceKey, selection.ToToken());
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Rebuilds a selection from the three separate keys shipped builds wrote. Any part that fails
    /// validation is dropped and the corresponding default is used, because a learner losing their
    /// text size is better than the app refusing to start on a stale preference.
    /// </summary>
    private bool TryLoadLegacy(out AppearanceSelection selection)
    {
        selection = null!;

        var themeId = _preferences.Get(LegacyThemeKey, string.Empty);
        var modeToken = _preferences.Get(LegacyModeKey, string.Empty);
        var fontScale = _preferences.Get(LegacyFontScaleKey, double.NaN);

        var hasAnything = !string.IsNullOrEmpty(themeId)
            || !string.IsNullOrEmpty(modeToken)
            || double.IsFinite(fontScale);

        if (!hasAnything)
        {
            return false;
        }

        var resolvedTheme = ThemeCatalog.Contains(themeId) ? themeId : ThemeCatalog.DefaultThemeId;
        var resolvedMode = ThemeModeExtensions.TryParse(modeToken, out var mode) ? mode : ThemeCatalog.DefaultMode;
        var resolvedScale = AppearanceSelection.IsValidFontScale(fontScale)
            ? fontScale
            : AppearanceSelection.DefaultFontScale;

        selection = new AppearanceSelection(resolvedTheme, resolvedMode, resolvedScale);
        _logger?.LogInformation("Adopted legacy per-key appearance preferences into the appearance token.");
        return true;
    }
}
