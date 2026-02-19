using MauiBootstrapTheme.Theming;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Manages app theme (color palette), mode (light/dark), and font scale.
/// Uses MauiBootstrapTheme for native Bootstrap-style theming.
/// Themes are registered at startup via UseBootstrapTheme() in MauiProgram.cs.
/// </summary>
public class NativeThemeService
{
    private const string PREF_THEME = "AppTheme";
    private const string PREF_MODE = "AppThemeMode";
    private const string PREF_FONT_SCALE = "AppFontScale";
    private const string DEFAULT_THEME = "seoul-pop";
    private const string DEFAULT_MODE = "dark";
    private const double DEFAULT_FONT_SCALE = 1.0;

    private readonly ILogger<NativeThemeService> _logger;
    private string _currentTheme;
    private string _currentMode;
    private double _fontScale;

    public event EventHandler<ThemeChangedEventArgs> ThemeChanged;

    public NativeThemeService(ILogger<NativeThemeService> logger)
    {
        _logger = logger;
        _currentTheme = Preferences.Default.Get(PREF_THEME, DEFAULT_THEME);
        _currentMode = Preferences.Default.Get(PREF_MODE, DEFAULT_MODE);
        _fontScale = Preferences.Default.Get(PREF_FONT_SCALE, DEFAULT_FONT_SCALE);
    }

    public string CurrentTheme => _currentTheme;
    public string CurrentMode => _currentMode;
    public double FontScale => _fontScale;
    public bool IsDarkMode => _currentMode == "dark";

    /// <summary>
    /// Returns the list of registered theme names from the BootstrapTheme registry.
    /// </summary>
    public IReadOnlyCollection<string> AvailableThemes => BootstrapTheme.RegisteredThemes;

    public void Initialize()
    {
        // Apply mode FIRST so the theme constructor picks up dark/light state
        ApplyMode(_currentMode);
        ApplyTheme(_currentTheme);
    }

    public void SetTheme(string theme)
    {
        if (_currentTheme == theme) return;
        _currentTheme = theme;
        Preferences.Default.Set(PREF_THEME, theme);
        ApplyTheme(theme);
        OnThemeChanged();
    }

    public void SetMode(string mode)
    {
        if (_currentMode == mode) return;
        _currentMode = mode;
        Preferences.Default.Set(PREF_MODE, mode);
        ApplyMode(mode);
        OnThemeChanged();
    }

    public void SetFontScale(double scale)
    {
        if (Math.Abs(_fontScale - scale) < 0.001) return;
        _fontScale = scale;
        Preferences.Default.Set(PREF_FONT_SCALE, scale);
        OnThemeChanged();
    }

    private void ApplyTheme(string themeName)
    {
        BootstrapTheme.Apply(themeName);
        _logger.LogInformation("Applied theme: {Theme}", themeName);
    }

    private void ApplyMode(string mode)
    {
        if (Application.Current != null)
        {
            Application.Current.UserAppTheme = mode == "dark" ? AppTheme.Dark : AppTheme.Light;
            
            // The ResourceDictionary's ApplyThemeMode updates DynamicResource values,
            // but BootstrapTheme.Current properties need re-syncing for native handlers
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                if (Application.Current?.Resources != null)
                {
                    BootstrapTheme.SyncFromResources(Application.Current.Resources);
                    BootstrapTheme.RefreshHandlers();
                }
            });
        }
    }

    private void OnThemeChanged()
    {
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_currentTheme, _currentMode, _fontScale));
    }

    /// <summary>
    /// Returns a human-readable display name for a theme identifier.
    /// </summary>
    public static string GetThemeDisplayName(string themeId) => themeId switch
    {
        "seoul-pop" => "Seoul Pop",
        "ocean" => "Ocean",
        "forest" => "Forest",
        "sunset" => "Sunset",
        "monochrome" => "Monochrome",
        "default" => "Default",
        "darkly" => "Darkly",
        "flatly" => "Flatly",
        "sketchy" => "Sketchy",
        "slate" => "Slate",
        "vapor" => "Vapor",
        "brite" => "Brite",
        _ => themeId
    };

    /// <summary>
    /// Returns the primary and accent swatch colors for a theme.
    /// Colors differ based on whether dark mode is active.
    /// </summary>
    public static (Color Primary, Color Accent) GetThemeSwatchColors(string themeId, bool isDark) => themeId switch
    {
        "seoul-pop" => isDark ? (Color.FromArgb("#6B8CFF"), Color.FromArgb("#FF7A4D")) : (Color.FromArgb("#1E4DFF"), Color.FromArgb("#FF6A3D")),
        "ocean" => isDark ? (Color.FromArgb("#22D3EE"), Color.FromArgb("#5EEAD4")) : (Color.FromArgb("#0891B2"), Color.FromArgb("#14B8A6")),
        "forest" => isDark ? (Color.FromArgb("#34D399"), Color.FromArgb("#FDE047")) : (Color.FromArgb("#059669"), Color.FromArgb("#FBBF24")),
        "sunset" => isDark ? (Color.FromArgb("#FB923C"), Color.FromArgb("#FBA7D8")) : (Color.FromArgb("#EA580C"), Color.FromArgb("#F472B6")),
        "monochrome" => isDark ? (Color.FromArgb("#D1D5DB"), Color.FromArgb("#F3F4F6")) : (Color.FromArgb("#374151"), Color.FromArgb("#1F2937")),
        "default" => (Color.FromArgb("#0d6efd"), Color.FromArgb("#6c757d")),
        "darkly" => (Color.FromArgb("#375a7f"), Color.FromArgb("#00bc8c")),
        "flatly" => (Color.FromArgb("#2c3e50"), Color.FromArgb("#18bc9c")),
        "sketchy" => (Color.FromArgb("#333333"), Color.FromArgb("#868e96")),
        "slate" => (Color.FromArgb("#3a3f44"), Color.FromArgb("#7a8288")),
        "vapor" => (Color.FromArgb("#6610f2"), Color.FromArgb("#e83e8c")),
        "brite" => (Color.FromArgb("#0d6efd"), Color.FromArgb("#e83e8c")),
        _ => (Color.FromArgb("#0d6efd"), Color.FromArgb("#6c757d"))
    };
}

public class ThemeChangedEventArgs : EventArgs
{
    public string Theme { get; }
    public string Mode { get; }
    public double FontScale { get; }

    public ThemeChangedEventArgs(string theme, string mode, double fontScale)
    {
        Theme = theme;
        Mode = mode;
        FontScale = fontScale;
    }
}
