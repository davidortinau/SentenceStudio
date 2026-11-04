using AndroidX.Compose;
using AndroidX.Compose.Material3;
using SentenceStudio.Contracts.Theme;
using ComposeMaterialTheme = AndroidX.Compose.MaterialTheme;

namespace SentenceStudio.Native.Android;

internal sealed record AndroidVisualPalette(
    Color Primary,
    Color OnPrimary,
    Color Accent,
    Color Background,
    Color Text,
    Color SecondaryText,
    Color Card,
    Color Elevated,
    Color Border,
    Color Success,
    Color Danger,
    Color DangerSurface,
    Color DangerContainer,
    Color OnDanger,
    Color WarningText,
    Color KnownText);

internal static class AndroidVisualTheme
{
    internal const int PagePadding = 16;
    internal const int CardPadding = 16;
    internal const int CardRadius = 16;
    internal const int ControlRadius = 10;
    internal const int ControlHeight = 48;
    internal const int CaptionSize = 12;
    internal const int QuizPromptSize = 42;

    private static AndroidVisualPalette Create(string themeId, bool dark)
    {
        var tokens = AndroidThemeTokensCatalog.For(themeId, dark);
        return new(
            Color.FromHex(tokens.Primary),
            Color.FromHex(tokens.OnPrimary),
            Color.FromHex(tokens.Accent),
            Color.FromHex(tokens.Background),
            Color.FromHex(tokens.Text),
            Color.FromHex(tokens.SecondaryText),
            Color.FromHex(tokens.Card),
            Color.FromHex(tokens.Elevated),
            Color.FromHex(tokens.Border),
            Color.FromHex("#12B886"),
            Color.FromHex("#E13B30"),
            Color.FromHex(tokens.DangerSurface),
            Color.FromHex(AndroidSamCards.DangerContainerHex),
            Color.FromHex(AndroidSamCards.OnDangerHex),
            Color.FromHex(tokens.WarningText),
            Color.FromHex(tokens.KnownText));
    }

    private static readonly Dictionary<(string Theme, bool Dark), AndroidVisualPalette> Palettes = [];

    /// <summary>The palette for the selected source theme (Settings) and the effective mode.</summary>
    internal static AndroidVisualPalette For(bool dark, string? themeId = null)
    {
        var id = ThemeCatalog.Contains(themeId) ? themeId! : ThemeCatalog.DefaultThemeId;
        lock (Palettes)
        {
            if (!Palettes.TryGetValue((id, dark), out var palette))
                Palettes[(id, dark)] = palette = Create(id, dark);
            return palette;
        }
    }

    /// <summary>Bootswatch themes keep one page surface in both modes; Material follows the surface.</summary>
    internal static bool UsesDarkScheme(bool dark, string? themeId) =>
        AndroidThemeTokensCatalog.For(themeId, dark).DarkSurface;

    internal static ColorScheme ColorScheme(bool dark, string? themeId = null)
    {
        var color = For(dark, themeId);
        return UsesDarkScheme(dark, themeId)
            ? ComposeMaterialTheme.DarkColorScheme(
                primary: color.Primary, onPrimary: color.OnPrimary,
                secondary: color.Success, background: color.Background,
                onBackground: color.Text, surface: color.Background,
                onSurface: color.Text, surfaceVariant: color.Card,
                onSurfaceVariant: color.SecondaryText, outline: color.Border,
                error: color.Danger, onError: Color.White)
            : ComposeMaterialTheme.LightColorScheme(
                primary: color.Primary, onPrimary: color.OnPrimary,
                secondary: color.Success, background: color.Background,
                onBackground: color.Text, surface: color.Background,
                onSurface: color.Text, surfaceVariant: color.Card,
                onSurfaceVariant: color.SecondaryText, outline: color.Border,
                error: color.Danger, onError: Color.White);
    }
}
