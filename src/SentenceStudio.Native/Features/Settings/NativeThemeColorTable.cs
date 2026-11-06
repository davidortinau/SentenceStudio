using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Native.Features.Settings;

/// <summary>One Bootstrap alert variant (fill, text and border) for one theme and mode.</summary>
internal sealed record NativeThemeAlertColors(string Fill, string Text, string Border);

/// <summary>
/// The web app's exact colours for one theme in one mode, as plain hex strings
/// (no platform UI types), so the iOS and Android heads read the same values.
/// </summary>
/// <param name="Primary"><c>--bs-primary</c>.</param>
/// <param name="Accent"><c>--ss-accent</c> (custom themes) or the catalogue accent.</param>
/// <param name="Secondary"><c>--bs-secondary</c>.</param>
/// <param name="Body"><c>--bs-body-bg</c>, the page surface.</param>
/// <param name="Text"><c>--bs-body-color</c>.</param>
/// <param name="SecondaryText"><c>--bs-secondary-color</c> (Bootswatch: body colour at 75% over the page).</param>
/// <param name="TertiaryText"><c>--bs-tertiary-color</c> (Bootswatch: body colour at 50% over the page).</param>
/// <param name="SecondarySurface"><c>--bs-secondary-bg</c>.</param>
/// <param name="CardSurface"><c>--bs-tertiary-bg</c>.</param>
/// <param name="Border"><c>--bs-border-color</c>.</param>
/// <param name="ElevatedSurface"><c>--ss-surface-elevated</c> (Bootswatch: <c>--bs-secondary-bg</c>).</param>
/// <param name="OnPrimary"><c>--ss-on-primary</c> (Bootswatch: the compiled <c>.btn-primary</c> colour).</param>
internal sealed record NativeThemeColors(
    string Primary, string Accent, string Secondary, string Body,
    string Text, string SecondaryText, string TertiaryText,
    string SecondarySurface, string CardSurface, string Border,
    string ElevatedSurface, string OnPrimary,
    NativeThemeAlertColors SuccessAlert, NativeThemeAlertColors DangerAlert,
    NativeThemeAlertColors InfoAlert)
{
    /// <summary>
    /// True when the page is darker than its text. Bootswatch Slate and Vapor keep a
    /// dark page in light mode, so the page, not the selected mode, decides this.
    /// </summary>
    internal bool IsDarkSurface =>
        SrgbColor.Parse(Body).RelativeLuminance < SrgbColor.Parse(Text).RelativeLuminance;
}

/// <summary>
/// Exact web colours for all ten themes, light and dark. Custom themes come from the
/// pinned <c>src/SentenceStudio.UI/wwwroot/css/app.css</c> blocks
/// <c>[data-ss-theme=...][data-bs-theme=light|dark]</c>; Bootswatch themes from
/// <c>wwwroot/css/themes/{flatly,sketchy,slate,vapor,brite}.min.css</c> (5.3.8)
/// <c>:root</c> and <c>[data-bs-theme=dark]</c> variables. Brand primary, accent and
/// page surface come from <see cref="ThemeCatalog"/>, which mirrors the same CSS.
/// <c>NativeThemeColorTableTests</c> compares every value with the CSS files.
/// </summary>
internal static class NativeThemeColorTable
{
    /// <summary>
    /// <c>--bs-success</c> and <c>--bs-warning</c> (<c>app.css</c> <c>:root</c>, the same in every
    /// theme and mode): the heads' Success and Warning roles and the encoding badge tints.
    /// </summary>
    internal const string SuccessHex = "#12B886";
    internal const string WarningHex = "#FFC233";

    /// <summary>The colours for <paramref name="themeId"/>; throws for an id not in the catalogue.</summary>
    internal static NativeThemeColors For(string themeId, bool dark)
    {
        var theme = ThemeCatalog.Get(themeId);
        var brand = theme.PaletteFor(dark ? ThemeMode.Dark : ThemeMode.Light);
        var css = Css(theme.Id, dark);
        var alerts = Alerts(theme.Id, dark);
        return new(brand.Primary.Hex, brand.Accent.Hex, Secondary(theme.Id, dark),
            brand.Surface.Hex, css.Text, css.SecondaryText, css.TertiaryText,
            css.SecondarySurface, css.CardSurface, css.Border, css.ElevatedSurface,
            css.OnPrimary, alerts.Success, alerts.Danger, alerts.Info);
    }

    /// <summary>Opaque <paramref name="foreground"/> at <paramref name="opacity"/> over <paramref name="background"/>.</summary>
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

    private sealed record CssRoles(
        string Text, string SecondaryText, string TertiaryText, string SecondarySurface,
        string CardSurface, string Border, string ElevatedSurface, string OnPrimary);

    // --bs-secondary. The Bootswatch value is the same in both modes.
    private static string Secondary(string theme, bool dark) => theme switch
    {
        "seoul-pop" => dark ? "#21D6A4" : "#12B886",
        "ocean" => dark ? "#2DD4BF" : "#06B6D4",
        "forest" => dark ? "#FBBF24" : "#F59E0B",
        "sunset" => dark ? "#F472B6" : "#DB2777",
        "monochrome" => dark ? "#9CA3AF" : "#6B7280",
        "flatly" => "#95A5A6",
        "sketchy" => "#555555",
        "slate" => "#7A8288",
        "vapor" => "#EA39B8",
        "brite" => "#FFFFFF",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme,
            "The theme colour table has no secondary colour for this theme.")
    };

    // Order: text, secondary text, tertiary text, secondary bg, card (tertiary bg),
    // border, elevated, on-primary.
    private static CssRoles Css(string theme, bool dark) => (theme, dark) switch
    {
        ("seoul-pop", false) => new("#0B0D12", "#394150", "#7A8699", "#FFFFFF",
            "#EEF1FF", "#D6DAF0", "#FCFDFF", "#FFFFFF"),
        ("seoul-pop", true) => new("#F8FAFF", "#C6D0E7", "#93A3C7", "#0C1222",
            "#121B33", "#263457", "#1A2545", "#081022"),
        ("ocean", false) => new("#0F172A", "#475569", "#94A3B8", "#FFFFFF",
            "#E0F2FE", "#BAE6FD", "#FCFEFF", "#FFFFFF"),
        ("ocean", true) => new("#F0F9FF", "#BAE6FD", "#7DD3FC", "#164E63",
            "#155E75", "#0E7490", "#1E3A4A", "#082F49"),
        ("forest", false) => new("#1C2B1F", "#4B5563", "#9CA3AF", "#FFFFFF",
            "#DCFCE7", "#BBF7D0", "#FDFFFE", "#FFFFFF"),
        ("forest", true) => new("#F0FDF4", "#BBF7D0", "#86EFAC", "#14532D",
            "#166534", "#15803D", "#1E3D29", "#052E16"),
        ("sunset", false) => new("#1C1917", "#57534E", "#A8A29E", "#FFFFFF",
            "#FFEDD5", "#FED7AA", "#FFFBFA", "#FFFFFF"),
        ("sunset", true) => new("#FFF7ED", "#FED7AA", "#FDBA74", "#431407",
            "#7C2D12", "#C2410C", "#52251A", "#431407"),
        ("monochrome", false) => new("#111827", "#4B5563", "#9CA3AF", "#F9FAFB",
            "#F3F4F6", "#E5E7EB", "#FEFEFE", "#FFFFFF"),
        ("monochrome", true) => new("#F9FAFB", "#D1D5DB", "#9CA3AF", "#1F2937",
            "#374151", "#4B5563", "#2D3748", "#111827"),
        ("flatly", false) => Bootswatch("#FFFFFF", "#212529", "#ECF0F1", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
        ("flatly", true) => Bootswatch("#212529", "#DEE2E6", "#343A40", "#2B3035", "#7B8A8B", "#FFFFFF"),
        ("sketchy", false) => Bootswatch("#FFFFFF", "#212529", "#F7F7F9", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
        ("sketchy", true) => Bootswatch("#212529", "#DEE2E6", "#333333", "#2A2C2E", "#555555", "#FFFFFF"),
        ("slate", false) => Bootswatch("#272B30", "#AAAAAA", "#E9ECEF", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
        ("slate", true) => Bootswatch("#272B30", "#DEE2E6", "#3A3F44", "#31353A", "#52575C", "#FFFFFF"),
        ("vapor", false) => Bootswatch("#1A0933", "#32FBE2", "#E9ECEF", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
        ("vapor", true) => Bootswatch("#170229", "#DEE2E6", "#343A40", "#261E35", "#495057", "#FFFFFF"),
        ("brite", false) => Bootswatch("#FFFFFF", "#212529", "#E9ECEF", "#F8F9FA", "#DEE2E6", "#000000"),
        ("brite", true) => Bootswatch("#212529", "#DEE2E6", "#343A40", "#2B3035", "#495057", "#000000"),
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme,
            "The theme colour table has no CSS mapping for this theme.")
    };

    // Bootstrap 5.3 derives --bs-secondary-color and --bs-tertiary-color as the body
    // colour at 75% and 50% alpha; native paints them opaque over the page. Bootswatch
    // has no --ss-surface-elevated, so elevated surfaces use --bs-secondary-bg.
    private static CssRoles Bootswatch(
        string body, string text, string secondaryBg, string tertiaryBg,
        string border, string onPrimary) =>
        new(text, Blend(text, body, 0.75), Blend(text, body, 0.5),
            secondaryBg, tertiaryBg, border, secondaryBg, onPrimary);

    // Bootstrap's alert variants read the emphasis, subtle-fill and subtle-border tokens
    // of the selected stylesheet: stock Bootstrap 5.3 for the custom themes, each
    // Bootswatch stylesheet otherwise. Order per variant: fill, text, border.
    private static (NativeThemeAlertColors Success, NativeThemeAlertColors Danger,
        NativeThemeAlertColors Info) Alerts(string theme, bool dark) => (theme, dark) switch
    {
        ("seoul-pop" or "ocean" or "forest" or "sunset" or "monochrome", false) =>
            (new("#D1E7DD", "#0A3622", "#A3CFBB"), new("#F8D7DA", "#58151C", "#F1AEB5"),
                new("#CFF4FC", "#055160", "#9EEAF9")),
        ("seoul-pop" or "ocean" or "forest" or "sunset" or "monochrome", true) =>
            (new("#051B11", "#75B798", "#0F5132"), new("#2C0B0E", "#EA868F", "#842029"),
                new("#032830", "#6EDFF6", "#087990")),
        ("flatly", false) =>
            (new("#D1F2EB", "#0A4B3E", "#A3E4D7"), new("#FADBD8", "#5C1E18", "#F5B7B1"),
                new("#D6EAF8", "#153D58", "#AED6F1")),
        ("flatly", true) =>
            (new("#05261F", "#74D7C4", "#0E715E"), new("#2E0F0C", "#F1948A", "#8B2E24"),
                new("#0A1E2C", "#85C1E9", "#1F5B83")),
        ("sketchy", false) =>
            (new("#D4EDDA", "#10431C", "#A9DCB5"), new("#F8D7DA", "#58151C", "#F1AEB5"),
                new("#D1ECF1", "#09414A", "#A2DAE3")),
        ("sketchy", true) =>
            (new("#08210E", "#7ECA8F", "#186429"), new("#2C0B0E", "#EA868F", "#842029"),
                new("#052025", "#74C7D4", "#0E616E")),
        ("slate", false) =>
            (new("#E0F3E0", "#274E27", "#C0E7C0"), new("#FCDFDE", "#5F2624", "#F8BFBD"),
                new("#DEF2F8", "#244D59", "#BDE6F2")),
        ("slate", true) =>
            (new("#142714", "#A1DCA1", "#3B763B"), new("#301312", "#F59F9D", "#8F3937"),
                new("#12262C", "#9DD9EB", "#377385")),
        ("vapor", false) =>
            (new("#D8FCE6", "#186134", "#B1FACD"), new("#FADBDD", "#5B1E22", "#F4B7BB"),
                new("#D1ECFD", "#0B4162", "#A4DAFB")),
        ("vapor", true) =>
            (new("#0C301A", "#8AF7B3", "#24914D"), new("#2E0F11", "#EF9499", "#892E33"),
                new("#052031", "#76C7FA", "#106194")),
        ("brite", false) =>
            (new("#E1F6E9", "#2A543A", "#C3EDD3"), new("#FDE0E0", "#622828", "#FBC1C1"),
                new("#D3F6FB", "#0E545F", "#A7EDF8")),
        ("brite", true) =>
            (new("#152A1D", "#A4E5BD", "#3E7F57"), new("#311414", "#F9A3A3", "#933D3D"),
                new("#072A2F", "#7AE4F4", "#147E8E")),
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme,
            "The theme colour table has no alert mapping for this theme.")
    };
}
