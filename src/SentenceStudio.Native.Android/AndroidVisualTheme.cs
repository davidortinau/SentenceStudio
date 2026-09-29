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

    private static AndroidVisualPalette Create(bool dark)
    {
        var brand = ThemeCatalog.Default.PaletteFor(dark ? ThemeMode.Dark : ThemeMode.Light);
        return new(
            Color.FromHex(brand.Primary.Hex),
            Color.FromHex(dark ? "#081022" : "#FFFFFF"),
            Color.FromHex(brand.Accent.Hex),
            Color.FromHex(brand.Surface.Hex),
            Color.FromHex(dark ? "#F8FAFF" : "#0B0D12"),
            Color.FromHex(dark ? "#C6D0E7" : "#394150"),
            Color.FromHex(dark ? "#121B33" : "#EEF1FF"),
            Color.FromHex(dark ? "#1A2545" : "#FCFDFF"),
            Color.FromHex(dark ? "#263457" : "#D6DAF0"),
            Color.FromHex("#12B886"),
            Color.FromHex("#E13B30"),
            Color.FromHex(dark ? "#401C27" : "#FFF0EF"),
            Color.FromHex(dark ? "#FFD77A" : "#855900"),
            Color.FromHex(dark ? "#63DCAA" : "#136F52"));
    }

    private static readonly AndroidVisualPalette Light = Create(false);
    private static readonly AndroidVisualPalette Dark = Create(true);

    internal static AndroidVisualPalette For(bool dark) => dark ? Dark : Light;

    internal static ColorScheme ColorScheme(bool dark)
    {
        var color = For(dark);
        return dark
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
