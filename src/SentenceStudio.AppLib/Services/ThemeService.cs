using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// Service for managing app theme (color palette) and mode (light/dark).
/// Persists user preferences via the platform preference abstraction.
/// Themes fall into two categories:
///   - Custom CSS-variable themes (seoul-pop, ocean, forest, sunset, monochrome) that override --bs-* on default Bootstrap
///   - Bootswatch themes (flatly, sketchy, slate, vapor, brite) that swap the entire Bootstrap CSS file
/// </summary>
public class ThemeService
{
    private readonly IPreferencesService _preferences;
    private const string PREF_THEME = "AppTheme";
    private const string PREF_MODE = "AppThemeMode";
    private const string PREF_FONT_SCALE = "AppFontScale";
    private const string DEFAULT_THEME = "seoul-pop";
    private const string DEFAULT_MODE = "dark";
    private const double DEFAULT_FONT_SCALE = 1.0;

    private string _currentTheme = DEFAULT_THEME;
    private string _currentMode = DEFAULT_MODE;
    private double _fontScale = DEFAULT_FONT_SCALE;

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public ThemeService(IPreferencesService preferences)
    {
        _preferences = preferences;
        _currentTheme = _preferences.Get(PREF_THEME, DEFAULT_THEME);
        _currentMode = _preferences.Get(PREF_MODE, DEFAULT_MODE);
        _fontScale = _preferences.Get(PREF_FONT_SCALE, DEFAULT_FONT_SCALE);
    }

    public string CurrentTheme => _currentTheme;
    public string CurrentMode => _currentMode;
    public double FontScale => _fontScale;

    public bool IsDarkMode => _currentMode == "dark";

    public void SetTheme(string theme)
    {
        if (_currentTheme == theme) return;

        _currentTheme = theme;
        _preferences.Set(PREF_THEME, theme);
        OnThemeChanged();
    }

    public void SetMode(string mode)
    {
        if (_currentMode == mode) return;

        _currentMode = mode;
        _preferences.Set(PREF_MODE, mode);
        OnThemeChanged();
    }

    public void SetFontScale(double scale)
    {
        if (Math.Abs(_fontScale - scale) < 0.001) return;

        _fontScale = scale;
        _preferences.Set(PREF_FONT_SCALE, scale);
        OnThemeChanged();
    }

    public void SetThemeAndMode(string theme, string mode)
    {
        bool changed = false;

        if (_currentTheme != theme)
        {
            _currentTheme = theme;
            _preferences.Set(PREF_THEME, theme);
            changed = true;
        }

        if (_currentMode != mode)
        {
            _currentMode = mode;
            _preferences.Set(PREF_MODE, mode);
            changed = true;
        }

        if (changed)
        {
            OnThemeChanged();
        }
    }

    private void OnThemeChanged()
    {
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_currentTheme, _currentMode, _fontScale));
    }

    /// <summary>
    /// Available theme names for UI selection.
    /// </summary>
    public static readonly string[] AvailableThemes = new[]
    {
        "seoul-pop",
        "ocean",
        "forest",
        "sunset",
        "monochrome",
        "flatly",
        "sketchy",
        "slate",
        "vapor",
        "brite"
    };

    /// <summary>
    /// Human-readable display names for themes.
    /// </summary>
    public static string GetThemeDisplayName(string theme)
    {
        return theme switch
        {
            "seoul-pop" => "Seoul Pop",
            "ocean" => "Ocean",
            "forest" => "Forest",
            "sunset" => "Sunset",
            "monochrome" => "Monochrome",
            "flatly" => "Flatly",
            "sketchy" => "Sketchy",
            "slate" => "Slate",
            "vapor" => "Vapor",
            "brite" => "Brite",
            _ => theme
        };
    }

    /// <summary>
    /// Preview color swatches for theme picker UI (primary + accent hex codes).
    /// </summary>
    public static (string Primary, string Accent) GetThemeColors(string theme, string mode)
    {
        return (theme, mode) switch
        {
            ("seoul-pop", "light") => ("#1E4DFF", "#FF6A3D"),
            ("seoul-pop", "dark") => ("#6B8CFF", "#FF7A4D"),
            ("ocean", "light") => ("#0891B2", "#14B8A6"),
            ("ocean", "dark") => ("#22D3EE", "#5EEAD4"),
            ("forest", "light") => ("#059669", "#FBBF24"),
            ("forest", "dark") => ("#34D399", "#FDE047"),
            ("sunset", "light") => ("#EA580C", "#F472B6"),
            ("sunset", "dark") => ("#FB923C", "#FBA7D8"),
            ("monochrome", "light") => ("#374151", "#1F2937"),
            ("monochrome", "dark") => ("#D1D5DB", "#F3F4F6"),
            ("flatly", _) => ("#2c3e50", "#18bc9c"),
            ("sketchy", _) => ("#333333", "#868e96"),
            ("slate", _) => ("#3a3f44", "#7a8288"),
            ("vapor", _) => ("#6f42c1", "#ea39b8"),
            ("brite", _) => ("#a2e436", "#ff7518"),
            _ => ("#6B8CFF", "#FF7A4D")
        };
    }
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
