namespace SentenceStudio.Services;

/// <summary>
/// Service for managing app theme (color palette) and mode (light/dark).
/// Persists user preferences via Microsoft.Maui.Storage.Preferences.
/// </summary>
public class ThemeService
{
    private const string PREF_THEME = "AppTheme";
    private const string PREF_MODE = "AppThemeMode";
    private const string DEFAULT_THEME = "seoul-pop";
    private const string DEFAULT_MODE = "dark";

    private string _currentTheme = DEFAULT_THEME;
    private string _currentMode = DEFAULT_MODE;

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public ThemeService()
    {
        // Load preferences on init
        _currentTheme = Preferences.Default.Get(PREF_THEME, DEFAULT_THEME);
        _currentMode = Preferences.Default.Get(PREF_MODE, DEFAULT_MODE);
    }

    public string CurrentTheme => _currentTheme;
    public string CurrentMode => _currentMode;

    public bool IsDarkMode => _currentMode == "dark";

    public void SetTheme(string theme)
    {
        if (_currentTheme == theme) return;

        _currentTheme = theme;
        Preferences.Default.Set(PREF_THEME, theme);
        OnThemeChanged();
    }

    public void SetMode(string mode)
    {
        if (_currentMode == mode) return;

        _currentMode = mode;
        Preferences.Default.Set(PREF_MODE, mode);
        OnThemeChanged();
    }

    public void SetThemeAndMode(string theme, string mode)
    {
        bool changed = false;

        if (_currentTheme != theme)
        {
            _currentTheme = theme;
            Preferences.Default.Set(PREF_THEME, theme);
            changed = true;
        }

        if (_currentMode != mode)
        {
            _currentMode = mode;
            Preferences.Default.Set(PREF_MODE, mode);
            changed = true;
        }

        if (changed)
        {
            OnThemeChanged();
        }
    }

    private void OnThemeChanged()
    {
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_currentTheme, _currentMode));
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
        "monochrome"
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
            _ => ("#6B8CFF", "#FF7A4D")
        };
    }
}

public class ThemeChangedEventArgs : EventArgs
{
    public string Theme { get; }
    public string Mode { get; }

    public ThemeChangedEventArgs(string theme, string mode)
    {
        Theme = theme;
        Mode = mode;
    }
}
