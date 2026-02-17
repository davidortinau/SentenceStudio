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
        ApplyTheme(_currentTheme);
        ApplyMode(_currentMode);
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
            Application.Current.UserAppTheme = mode == "dark" ? AppTheme.Dark : AppTheme.Light;
    }

    private void OnThemeChanged()
    {
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_currentTheme, _currentMode, _fontScale));
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
