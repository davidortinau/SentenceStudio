using AndroidX.Compose;
using AndroidX.Compose.Material3;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Native.Features.Vocabulary;
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
    Color KnownText)
{
    /// <summary>Theme-tinted fill for a selected segment and the selected tab indicator.</summary>
    internal Color SelectedContainer { get; init; }
    internal Color TertiaryText { get; init; }
    internal Color SecondarySurface { get; init; }
    internal Color Secondary { get; init; }

    /// <summary><c>--bs-warning</c>, the iOS head's Warning role.</summary>
    internal Color Warning { get; init; }

    /// <summary>The web app's colours for this theme and mode, as hex (shared with iOS).</summary>
    internal NativeThemeColors Theme { get; init; } = null!;
}

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
            Color.FromHex(tokens.KnownText))
        {
            SelectedContainer = Color.FromHex(tokens.SelectedContainer),
            TertiaryText = Color.FromHex(tokens.TertiaryText),
            SecondarySurface = Color.FromHex(tokens.SecondarySurface),
            Secondary = Color.FromHex(tokens.Secondary),
            Warning = Color.FromHex(VocabularyEncodingBadge.WarningHex),
            Theme = NativeThemeColorTable.For(themeId, dark),
        };
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

    /// <summary>
    /// Material roles from the theme's exact colours. Every container and surface-container
    /// role is set, so no component falls back to Material's baseline purple: segmented
    /// buttons and the navigation-bar indicator read secondaryContainer, the navigation
    /// bar, menus, sheets and dialogs read the surface containers.
    /// </summary>
    internal static ColorScheme ColorScheme(bool dark, string? themeId = null)
    {
        var color = For(dark, themeId);
        return UsesDarkScheme(dark, themeId)
            ? ComposeMaterialTheme.DarkColorScheme(
                primary: color.Primary, onPrimary: color.OnPrimary,
                primaryContainer: color.SelectedContainer, onPrimaryContainer: color.Text,
                inversePrimary: color.Primary,
                secondary: color.Success,
                secondaryContainer: color.SelectedContainer, onSecondaryContainer: color.Text,
                tertiary: color.Accent,
                tertiaryContainer: color.SelectedContainer, onTertiaryContainer: color.Text,
                background: color.Background, onBackground: color.Text,
                surface: color.Background, onSurface: color.Text,
                surfaceVariant: color.Card, onSurfaceVariant: color.SecondaryText,
                surfaceTint: color.Primary,
                inverseSurface: color.Text, inverseOnSurface: color.Background,
                error: color.Danger, onError: Color.White,
                errorContainer: color.DangerSurface, onErrorContainer: color.Text,
                outline: color.Border, outlineVariant: color.Border,
                surfaceBright: color.Elevated,
                surfaceContainer: color.Card, surfaceContainerHigh: color.Elevated,
                surfaceContainerHighest: color.Elevated, surfaceContainerLow: color.Card,
                surfaceContainerLowest: color.Background, surfaceDim: color.Background)
            : ComposeMaterialTheme.LightColorScheme(
                primary: color.Primary, onPrimary: color.OnPrimary,
                primaryContainer: color.SelectedContainer, onPrimaryContainer: color.Text,
                inversePrimary: color.Primary,
                secondary: color.Success,
                secondaryContainer: color.SelectedContainer, onSecondaryContainer: color.Text,
                tertiary: color.Accent,
                tertiaryContainer: color.SelectedContainer, onTertiaryContainer: color.Text,
                background: color.Background, onBackground: color.Text,
                surface: color.Background, onSurface: color.Text,
                surfaceVariant: color.Card, onSurfaceVariant: color.SecondaryText,
                surfaceTint: color.Primary,
                inverseSurface: color.Text, inverseOnSurface: color.Background,
                error: color.Danger, onError: Color.White,
                errorContainer: color.DangerSurface, onErrorContainer: color.Text,
                outline: color.Border, outlineVariant: color.Border,
                surfaceBright: color.Elevated,
                surfaceContainer: color.Card, surfaceContainerHigh: color.Elevated,
                surfaceContainerHighest: color.Elevated, surfaceContainerLow: color.Card,
                surfaceContainerLowest: color.Background, surfaceDim: color.Background);
    }
}
