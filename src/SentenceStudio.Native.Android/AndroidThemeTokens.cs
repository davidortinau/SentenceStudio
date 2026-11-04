using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;

namespace SentenceStudio.Native.Android;

/// <summary>Hex colour roles for one theme and mode (no Compose types, so tests can check them).</summary>
internal sealed record AndroidThemeTokens(
    string Primary, string OnPrimary, string Accent, string Background,
    string Text, string SecondaryText, string Card, string Elevated,
    string Border, string DangerSurface, string WarningText, string KnownText,
    bool DarkSurface)
{
    /// <summary><c>--bs-secondary</c>.</summary>
    internal string Secondary { get; init; } = "";

    /// <summary><c>--bs-tertiary-color</c>.</summary>
    internal string TertiaryText { get; init; } = "";

    /// <summary><c>--bs-secondary-bg</c>.</summary>
    internal string SecondarySurface { get; init; } = "";

    /// <summary>
    /// Fill of a selected segmented button and of the selected navigation-bar
    /// indicator: the theme primary at 20% over the page, so a primary icon or the
    /// body text stays legible on it (Material's default is a fixed purple).
    /// </summary>
    internal string SelectedContainer { get; init; } = "";
}

/// <summary>
/// Source Settings theme catalogue on Android: the web app's exact colours for all
/// ten themes in both modes, read from the shared <see cref="NativeThemeColorTable"/>
/// (the same table the iOS head reads). Only the Android-specific danger surface,
/// warning and known-word text roles, which the web has no variable for, are chosen
/// here from whether the page is dark.
/// </summary>
internal static class AndroidThemeTokensCatalog
{
    internal const double SelectedContainerOpacity = 0.2;

    internal static AndroidThemeTokens For(string? themeId, bool dark)
    {
        var id = ThemeCatalog.Contains(themeId) ? themeId! : ThemeCatalog.DefaultThemeId;
        var colors = NativeThemeColorTable.For(id, dark);
        var darkSurface = colors.IsDarkSurface;
        return new(
            colors.Primary,
            colors.OnPrimary,
            colors.Accent,
            colors.Body,
            colors.Text,
            colors.SecondaryText,
            colors.CardSurface,
            colors.ElevatedSurface,
            colors.Border,
            darkSurface ? "#401C27" : "#FFF0EF",
            darkSurface ? "#FFD77A" : "#855900",
            darkSurface ? "#63DCAA" : "#136F52",
            darkSurface)
        {
            Secondary = colors.Secondary,
            TertiaryText = colors.TertiaryText,
            SecondarySurface = colors.SecondarySurface,
            SelectedContainer = Blend(colors.Primary, colors.Body, SelectedContainerOpacity),
        };
    }

    internal static string Blend(string foreground, string background, double opacity) =>
        NativeThemeColorTable.Blend(foreground, background, opacity);
}
