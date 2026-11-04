using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Native.Android;

/// <summary>Hex colour roles for one theme and mode (no Compose types, so tests can check them).</summary>
internal sealed record AndroidThemeTokens(
    string Primary, string OnPrimary, string Accent, string Background,
    string Text, string SecondaryText, string Card, string Elevated,
    string Border, string DangerSurface, string WarningText, string KnownText,
    bool DarkSurface);

/// <summary>
/// Source Settings theme catalogue on Android. Seoul Pop keeps the exact pinned
/// app.css roles the Android head already used; the other nine themes take the
/// shared <see cref="ThemeCatalog"/> primary, accent and page surface and derive
/// text, card and border roles from that surface, keeping text at WCAG AA and
/// primary marks at 3:1 or better on the page.
/// </summary>
internal static class AndroidThemeTokensCatalog
{
    private const string LightText = "#0B0D12";
    private const string DarkText = "#F8FAFF";

    internal static AndroidThemeTokens For(string? themeId, bool dark)
    {
        var theme = ThemeCatalog.TryGet(themeId, out var found) ? found : ThemeCatalog.Default;
        var brand = theme.PaletteFor(dark ? ThemeMode.Dark : ThemeMode.Light);
        if (theme.Id == ThemeCatalog.DefaultThemeId)
            return new(
                brand.Primary.Hex,
                dark ? "#081022" : "#FFFFFF",
                brand.Accent.Hex,
                brand.Surface.Hex,
                dark ? DarkText : LightText,
                dark ? "#C6D0E7" : "#394150",
                dark ? "#121B33" : "#EEF1FF",
                dark ? "#1A2545" : "#FCFDFF",
                dark ? "#263457" : "#D6DAF0",
                dark ? "#401C27" : "#FFF0EF",
                dark ? "#FFD77A" : "#855900",
                dark ? "#63DCAA" : "#136F52",
                dark);

        var surface = brand.Surface;
        // Bootswatch palettes are fixed across modes, so the page surface, not the
        // selected mode, decides whether text is light or dark.
        var darkSurface = surface.ContrastRatio(SrgbColor.Parse(DarkText))
            > surface.ContrastRatio(SrgbColor.Parse(LightText));
        var text = darkSurface ? DarkText : LightText;
        var primary = Legible(brand.Primary, surface)
            ?? Legible(brand.Accent, surface) ?? SrgbColor.Parse(text);
        var onPrimary = ThemeContrast.Between(primary, primary).ReadableTextOnForeground;
        return new(
            primary.Hex,
            onPrimary,
            brand.Accent.Hex,
            surface.Hex,
            text,
            Blend(text, surface.Hex, 0.75),
            Blend(primary.Hex, surface.Hex, darkSurface ? 0.16 : 0.08),
            Blend("#FFFFFF", surface.Hex, darkSurface ? 0.08 : 0.7),
            Blend(primary.Hex, surface.Hex, darkSurface ? 0.34 : 0.22),
            darkSurface ? "#401C27" : "#FFF0EF",
            darkSurface ? "#FFD77A" : "#855900",
            darkSurface ? "#63DCAA" : "#136F52",
            darkSurface);
    }

    private static SrgbColor? Legible(SrgbColor color, SrgbColor surface) =>
        color.ContrastRatio(surface) >= 3.0 ? color : null;

    internal static string Blend(string foreground, string background, double opacity)
    {
        var fg = SrgbColor.Parse(foreground);
        var bg = SrgbColor.Parse(background);
        static int Channel(byte front, byte back, double alpha) =>
            (int)Math.Round(front * alpha + back * (1 - alpha));
        return $"#{Channel(fg.R, bg.R, opacity):X2}"
            + $"{Channel(fg.G, bg.G, opacity):X2}"
            + $"{Channel(fg.B, bg.B, opacity):X2}";
    }
}
