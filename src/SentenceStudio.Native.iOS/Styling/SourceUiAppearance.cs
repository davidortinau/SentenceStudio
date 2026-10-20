using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Native.iOS.Styling;

internal readonly record struct SourceColorPair(string Light, string Dark);

internal enum SourceButtonGroupPosition { None, First, Middle, Last }
internal enum SourceButtonGroupClass { Custom, Outline, Small }

internal readonly record struct SourceButtonCornerRadii(
    double TopLeftX, double TopLeftY, double TopRightX, double TopRightY,
    double BottomRightX, double BottomRightY, double BottomLeftX, double BottomLeftY);

internal sealed record SourceCssPalette(
    string Text,
    string SecondaryText,
    string TertiaryText,
    string SecondarySurface,
    string CardSurface,
    string Border,
    string ElevatedSurface,
    string OnPrimary);

internal sealed record SourceAlertPalette(string Fill, string Text, string Border);

internal sealed class SourceUiAppearance
{
    internal const string PreferenceKey = "AppAppearance";

    private SourceUiAppearance(AppearanceSelection selection)
    {
        Selection = selection;
        (Light, Dark) = CssFor(selection.ThemeId);
        (LightAlerts, DarkAlerts) = AlertsFor(selection.ThemeId);
    }

    internal AppearanceSelection Selection { get; }
    internal SourceCssPalette Light { get; }
    internal SourceCssPalette Dark { get; }
    private AlertModes LightAlerts { get; }
    private AlertModes DarkAlerts { get; }

    internal SourceColorPair BodyBackground => new(
        Selection.Theme.Light.Surface.Hex, Selection.Theme.Dark.Surface.Hex);
    internal SourceColorPair Primary => new(
        Selection.Theme.Light.Primary.Hex, Selection.Theme.Dark.Primary.Hex);
    internal SourceColorPair Accent => new(
        Selection.Theme.Light.Accent.Hex, Selection.Theme.Dark.Accent.Hex);
    internal SourceColorPair Text => Pair(p => p.Text);
    internal SourceColorPair SecondaryText => Pair(p => p.SecondaryText);
    internal SourceColorPair TertiaryText => Pair(p => p.TertiaryText);
    internal SourceColorPair SecondarySurface => Pair(p => p.SecondarySurface);
    internal SourceColorPair CardSurface => Pair(p => p.CardSurface);
    internal SourceColorPair ListRowSurface => BodyBackground;
    internal SourceColorPair PlainCardSurface => Selection.ThemeId == "slate"
        ? new("#32383E", "#32383E") : BodyBackground;
    internal SourceColorPair PlainCardText => Selection.ThemeId == "vapor"
        ? new("#FFFFFF", "#FFFFFF") : Text;
    internal SourceColorPair ElevatedSurface => Pair(p => p.ElevatedSurface);
    internal SourceColorPair Border => Pair(p => p.Border);
    // The Resource Edit border-secondary utility reads --bs-secondary,
    // unlike the source's normal --bs-border-color token.
    internal SourceColorPair BorderSecondary => Selection.ThemeId switch
    {
        "seoul-pop" => new("#12B886", "#21D6A4"),
        "ocean" => new("#06B6D4", "#2DD4BF"),
        "forest" => new("#F59E0B", "#FBBF24"),
        "sunset" => new("#DB2777", "#F472B6"),
        "monochrome" => new("#6B7280", "#9CA3AF"),
        _ => BootstrapSecondary
    };
    internal SourceColorPair OnPrimary => Pair(p => p.OnPrimary);
    internal SourceColorPair ProgressTrack => SecondarySurface;
    // Login's .btn-primary reads the selected Bootstrap stylesheet, not --bs-primary.
    internal SourceColorPair BootstrapPrimaryFill => Selection.ThemeId switch
    {
        "flatly" => new("#2C3E50", "#2C3E50"),
        "sketchy" => new("#333333", "#F8F9FA"),
        "slate" => new("#3A3F44", "#3A3F44"),
        "vapor" => new("#6F42C1", "#6F42C1"),
        "brite" => new("#A2E436", "#A2E436"),
        _ => new("#0D6EFD", "#0D6EFD")
    };
    internal SourceColorPair BootstrapPrimaryDisabledFill =>
        Selection.ThemeId == "sketchy"
            ? new("#333333", "#AAAAAA") : BootstrapPrimaryFill;
    internal SourceColorPair BootstrapPrimaryBorder => Selection.ThemeId switch
    {
        "sketchy" => new("#333333", "#333333"),
        "brite" => new("#000000", "#000000"),
        _ => BootstrapPrimaryFill
    };
    // Brite's .btn:disabled outranks the later plain .btn border override.
    internal SourceColorPair BootstrapPrimaryDisabledBorder =>
        Selection.ThemeId == "brite" ? BootstrapPrimaryFill : BootstrapPrimaryBorder;
    internal double BootstrapBaseButtonBorderWidth =>
        Selection.ThemeId is "brite" or "sketchy" ? 2 : 1;
    internal double BootstrapPrimaryBorderWidth => BootstrapBaseButtonBorderWidth;
    internal SourceColorPair BootstrapButtonBorder => Selection.ThemeId == "brite"
        ? new("#000000", "#000000") : Border;
    internal SourceColorPair BootstrapNeutralDisabledBorder => Selection.ThemeId switch
    {
        "brite" => new("#FFFFFF", "#FFFFFF"),
        "sketchy" => new("#555555", "#555555"),
        _ => BootstrapButtonBorder
    };
    internal SourceColorPair? BootstrapOutlineSecondaryFill =>
        Selection.ThemeId == "brite"
            ? new("#FFFFFF", "#FFFFFF") : null;
    internal SourceColorPair BootstrapPrimaryForeground => Selection.ThemeId switch
    {
        "sketchy" => new("#FFFFFF", "#000000"),
        "brite" => new("#000000", "#000000"),
        _ => new("#FFFFFF", "#FFFFFF")
    };
    // app.css overrides outline-success text/border only in dark mode.
    internal SourceColorPair BootstrapSuccessOutlineBorder => Selection.ThemeId switch
    {
        "flatly" => new("#18BC9C", "#12B886"),
        "sketchy" => new("#28A745", "#12B886"),
        "slate" => new("#62C462", "#12B886"),
        "vapor" => new("#3CF281", "#12B886"),
        "brite" => new("#000000", "#12B886"),
        _ => new("#198754", "#12B886")
    };
    internal SourceColorPair BootstrapSuccessOutlineForeground =>
        Selection.ThemeId switch
        {
            "vapor" => new("#FFFFFF", "#12B886"),
            "brite" => new("#000000", "#12B886"),
            _ => BootstrapSuccessOutlineBorder
        };
    internal SourceColorPair? BootstrapSuccessOutlineFill =>
        Selection.ThemeId == "brite"
            ? new("#68D391", "#68D391") : null;
    // Vapor's outline rule and Brite/Sketchy's base .btn specify a 2px stroke.
    internal double BootstrapSuccessOutlineBorderWidth =>
        Selection.ThemeId == "vapor" ? 2 : BootstrapBaseButtonBorderWidth;
    internal double BootstrapSmallButtonRadius => Selection.ThemeId switch
    {
        "sketchy" => 15,
        "vapor" => 0.8,
        _ => 4
    };
    internal double BootstrapSmallButtonHorizontalPadding(double viewportWidth) =>
        viewportWidth <= 767.98 ? 16
            : Selection.ThemeId == "brite" ? 12 : 8;
    internal static double BootstrapCustomButtonHorizontalPadding(double viewportWidth) =>
        viewportWidth <= 767.98 ? 16 : 14;
    internal SourceButtonCornerRadii GroupCorners(
        SourceButtonGroupClass buttonClass, SourceButtonGroupPosition position)
    {
        if (position == SourceButtonGroupPosition.None)
            throw new ArgumentOutOfRangeException(nameof(position));

        var ellipse = Selection.ThemeId == "sketchy"
            && buttonClass is (SourceButtonGroupClass.Outline or SourceButtonGroupClass.Small);
        var radius = buttonClass == SourceButtonGroupClass.Small
            ? BootstrapSmallButtonRadius : BootstrapRadius;
        var first = position == SourceButtonGroupPosition.First;
        var last = position == SourceButtonGroupPosition.Last;
        return new(
            first ? (ellipse ? 255 : radius) : 0, first ? (ellipse ? 25 : radius) : 0,
            last ? (ellipse ? 25 : radius) : 0, last ? (ellipse ? 225 : radius) : 0,
            last ? (ellipse ? 225 : radius) : 0, last ? (ellipse ? 25 : radius) : 0,
            first ? (ellipse ? 25 : radius) : 0, first ? (ellipse ? 255 : radius) : 0);
    }
    internal SourceColorPair BootstrapSecondary => Selection.ThemeId switch
    {
        "flatly" => new("#95A5A6", "#95A5A6"),
        "sketchy" => new("#555555", "#555555"),
        "slate" => new("#7A8288", "#7A8288"),
        "vapor" => new("#EA39B8", "#EA39B8"),
        "brite" => new("#FFFFFF", "#FFFFFF"),
        _ => new("#6C757D", "#6C757D")
    };
    internal SourceColorPair OnBootstrapSecondary =>
        Selection.ThemeId == "brite"
            ? new("#000000", "#000000")
            : new("#FFFFFF", "#FFFFFF");
    internal SourceColorPair InactiveStepDot
    {
        get
        {
            var secondary = Selection.ThemeId switch
            {
                "seoul-pop" => new SourceColorPair("#12B886", "#21D6A4"),
                "ocean" => new SourceColorPair("#06B6D4", "#2DD4BF"),
                "forest" => new SourceColorPair("#F59E0B", "#FBBF24"),
                "sunset" => new SourceColorPair("#DB2777", "#F472B6"),
                "monochrome" => new SourceColorPair("#6B7280", "#9CA3AF"),
                "flatly" => new SourceColorPair("#95A5A6", "#95A5A6"),
                "sketchy" => new SourceColorPair("#555555", "#555555"),
                "slate" => new SourceColorPair("#7A8288", "#7A8288"),
                "vapor" => new SourceColorPair("#EA39B8", "#EA39B8"),
                "brite" => new SourceColorPair("#FFFFFF", "#FFFFFF"),
                _ => throw new ArgumentOutOfRangeException(nameof(Selection.ThemeId))
            };
            return new(Blend(secondary.Light, BodyBackground.Light, 0.3),
                Blend(secondary.Dark, BodyBackground.Dark, 0.3));
        }
    }
    internal SourceColorPair Success => new("#12B886", "#12B886");
    internal SourceColorPair Danger => new("#E13B30", "#E13B30");
    internal SourceColorPair Warning => new("#FFC233", "#FFC233");
    internal SourceColorPair SuccessAlertFill =>
        new(LightAlerts.Success.Fill, DarkAlerts.Success.Fill);
    internal SourceColorPair SuccessAlertText =>
        new(LightAlerts.Success.Text, DarkAlerts.Success.Text);
    internal SourceColorPair SuccessAlertBorder =>
        new(LightAlerts.Success.Border, DarkAlerts.Success.Border);
    internal SourceColorPair DangerAlertFill =>
        new(LightAlerts.Danger.Fill, DarkAlerts.Danger.Fill);
    internal SourceColorPair DangerAlertText =>
        new(LightAlerts.Danger.Text, DarkAlerts.Danger.Text);
    internal SourceColorPair DangerAlertBorder =>
        new(LightAlerts.Danger.Border, DarkAlerts.Danger.Border);
    internal SourceColorPair InfoAlertFill =>
        new(LightAlerts.Info.Fill, DarkAlerts.Info.Fill);
    internal SourceColorPair InfoAlertText =>
        new(LightAlerts.Info.Text, DarkAlerts.Info.Text);
    internal SourceColorPair InfoAlertBorder =>
        new(LightAlerts.Info.Border, DarkAlerts.Info.Border);
    internal double BootstrapRadius => Selection.ThemeId switch
    {
        "sketchy" => 25,
        "vapor" => 2.4,
        _ => 6
    };

    internal string? FontPostScriptName(int weight,
        bool sourceHeading = false, bool strongElement = false) =>
        Selection.ThemeId switch
        {
            "flatly" or "vapor" => weight >= 600
                ? "Lato-Bold" : "Lato-Regular",
            "sketchy" when sourceHeading || strongElement => weight >= 600
                ? "CabinSketch-Bold" : "CabinSketch-Regular",
            "sketchy" => "Neucha",
            _ => null
        };

    internal static SourceUiAppearance FromSelection(AppearanceSelection selection) =>
        new(selection);

    internal static SourceUiAppearance Save(
        IPreferencesService preferences, AppearanceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(selection);
        preferences.Set(PreferenceKey, selection.ToToken());
        return new(selection);
    }

    // Same read-only token/legacy normalization as DevicePreferencesThemeStore
    // in the original AppLib. Do not write a preference while simply rendering.
    internal static SourceUiAppearance Load(IPreferencesService preferences,
        string? controlledPreviewToken = null,
        bool preferStoredPreviewAppearance = false)
    {
        if (preferStoredPreviewAppearance
            && AppearanceSelection.TryParse(
                preferences.Get(PreferenceKey, string.Empty), out var stored))
            return new(stored);

        if (controlledPreviewToken is not null)
        {
            if (!AppearanceSelection.TryParse(controlledPreviewToken,
                    out var previewSelection))
                throw new ArgumentException(
                    "The isolated Debug preview appearance token is invalid.",
                    nameof(controlledPreviewToken));
            return new(previewSelection);
        }

        var token = preferences.Get(PreferenceKey, string.Empty);
        if (AppearanceSelection.TryParse(token, out var selection))
            return new(selection);

        var themeId = preferences.Get("AppTheme", string.Empty);
        var modeToken = preferences.Get("AppThemeMode", string.Empty);
        var fontScale = preferences.Get("AppFontScale", double.NaN);
        if (string.IsNullOrEmpty(themeId) && string.IsNullOrEmpty(modeToken)
            && !double.IsFinite(fontScale))
            return new(AppearanceSelection.Default);

        var theme = ThemeCatalog.Contains(themeId)
            ? themeId : ThemeCatalog.DefaultThemeId;
        var mode = ThemeModeExtensions.TryParse(modeToken, out var parsedMode)
            ? parsedMode : ThemeCatalog.DefaultMode;
        var scale = AppearanceSelection.IsValidFontScale(fontScale)
            ? fontScale : AppearanceSelection.DefaultFontScale;
        return new(new AppearanceSelection(theme, mode, scale));
    }

    private SourceColorPair Pair(Func<SourceCssPalette, string> color) =>
        new(color(Light), color(Dark));

    // Bootstrap's alert variants read emphasis, subtle-fill and subtle-border
    // tokens from the selected iOS stylesheet, not the primary accent or .card-ss.
    private static (AlertModes Light, AlertModes Dark) AlertsFor(string theme) =>
        theme switch
        {
            "seoul-pop" or "ocean" or "forest" or "sunset" or "monochrome" => (
                new(new("#D1E7DD", "#0A3622", "#A3CFBB"),
                    new("#F8D7DA", "#58151C", "#F1AEB5"),
                    new("#CFF4FC", "#055160", "#9EEAF9")),
                new(new("#051B11", "#75B798", "#0F5132"),
                    new("#2C0B0E", "#EA868F", "#842029"),
                    new("#032830", "#6EDFF6", "#087990"))),
            "flatly" => (
                new(new("#D1F2EB", "#0A4B3E", "#A3E4D7"),
                    new("#FADBD8", "#5C1E18", "#F5B7B1"),
                    new("#D6EAF8", "#153D58", "#AED6F1")),
                new(new("#05261F", "#74D7C4", "#0E715E"),
                    new("#2E0F0C", "#F1948A", "#8B2E24"),
                    new("#0A1E2C", "#85C1E9", "#1F5B83"))),
            "sketchy" => (
                new(new("#D4EDDA", "#10431C", "#A9DCB5"),
                    new("#F8D7DA", "#58151C", "#F1AEB5"),
                    new("#D1ECF1", "#09414A", "#A2DAE3")),
                new(new("#08210E", "#7ECA8F", "#186429"),
                    new("#2C0B0E", "#EA868F", "#842029"),
                    new("#052025", "#74C7D4", "#0E616E"))),
            "slate" => (
                new(new("#E0F3E0", "#274E27", "#C0E7C0"),
                    new("#FCDFDE", "#5F2624", "#F8BFBD"),
                    new("#DEF2F8", "#244D59", "#BDE6F2")),
                new(new("#142714", "#A1DCA1", "#3B763B"),
                    new("#301312", "#F59F9D", "#8F3937"),
                    new("#12262C", "#9DD9EB", "#377385"))),
            "vapor" => (
                new(new("#D8FCE6", "#186134", "#B1FACD"),
                    new("#FADBDD", "#5B1E22", "#F4B7BB"),
                    new("#D1ECFD", "#0B4162", "#A4DAFB")),
                new(new("#0C301A", "#8AF7B3", "#24914D"),
                    new("#2E0F11", "#EF9499", "#892E33"),
                    new("#052031", "#76C7FA", "#106194"))),
            "brite" => (
                new(new("#E1F6E9", "#2A543A", "#C3EDD3"),
                    new("#FDE0E0", "#622828", "#FBC1C1"),
                    new("#D3F6FB", "#0E545F", "#A7EDF8")),
                new(new("#152A1D", "#A4E5BD", "#3E7F57"),
                    new("#311414", "#F9A3A3", "#933D3D"),
                    new("#072A2F", "#7AE4F4", "#147E8E"))),
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme,
                "The source appearance catalogue has no alert mapping for this theme.")
        };

    private sealed record AlertModes(
        SourceAlertPalette Success, SourceAlertPalette Danger, SourceAlertPalette Info);

    // Pinned app.css:64-389 for custom themes; Bootswatch 5.3.8
    // themes/{flatly,sketchy,slate,vapor,brite}.min.css :root/dark variables.
    // Brand primary, accent and page surface come from the original ThemeCatalog.
    private static (SourceCssPalette Light, SourceCssPalette Dark) CssFor(string theme) =>
        theme switch
        {
            "seoul-pop" => (
                new("#0B0D12", "#394150", "#7A8699", "#FFFFFF",
                    "#EEF1FF", "#D6DAF0", "#FCFDFF", "#FFFFFF"),
                new("#F8FAFF", "#C6D0E7", "#93A3C7", "#0C1222",
                    "#121B33", "#263457", "#1A2545", "#081022")),
            "ocean" => (
                new("#0F172A", "#475569", "#94A3B8", "#FFFFFF",
                    "#E0F2FE", "#BAE6FD", "#FCFEFF", "#FFFFFF"),
                new("#F0F9FF", "#BAE6FD", "#7DD3FC", "#164E63",
                    "#155E75", "#0E7490", "#1E3A4A", "#082F49")),
            "forest" => (
                new("#1C2B1F", "#4B5563", "#9CA3AF", "#FFFFFF",
                    "#DCFCE7", "#BBF7D0", "#FDFFFE", "#FFFFFF"),
                new("#F0FDF4", "#BBF7D0", "#86EFAC", "#14532D",
                    "#166534", "#15803D", "#1E3D29", "#052E16")),
            "sunset" => (
                new("#1C1917", "#57534E", "#A8A29E", "#FFFFFF",
                    "#FFEDD5", "#FED7AA", "#FFFBFA", "#FFFFFF"),
                new("#FFF7ED", "#FED7AA", "#FDBA74", "#431407",
                    "#7C2D12", "#C2410C", "#52251A", "#431407")),
            "monochrome" => (
                new("#111827", "#4B5563", "#9CA3AF", "#F9FAFB",
                    "#F3F4F6", "#E5E7EB", "#FEFEFE", "#FFFFFF"),
                new("#F9FAFB", "#D1D5DB", "#9CA3AF", "#1F2937",
                    "#374151", "#4B5563", "#2D3748", "#111827")),
            "flatly" => (
                Swap("#FFFFFF", "#212529", "#ECF0F1", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
                Swap("#212529", "#DEE2E6", "#343A40", "#2B3035", "#7B8A8B", "#FFFFFF")),
            "sketchy" => (
                Swap("#FFFFFF", "#212529", "#F7F7F9", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
                Swap("#212529", "#DEE2E6", "#333333", "#2A2C2E", "#555555", "#FFFFFF")),
            "slate" => (
                Swap("#272B30", "#AAAAAA", "#E9ECEF", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
                Swap("#272B30", "#DEE2E6", "#3A3F44", "#31353A", "#52575C", "#FFFFFF")),
            "vapor" => (
                Swap("#1A0933", "#32FBE2", "#E9ECEF", "#F8F9FA", "#DEE2E6", "#FFFFFF"),
                Swap("#170229", "#DEE2E6", "#343A40", "#261E35", "#495057", "#FFFFFF")),
            "brite" => (
                Swap("#FFFFFF", "#212529", "#E9ECEF", "#F8F9FA", "#DEE2E6", "#000000"),
                Swap("#212529", "#DEE2E6", "#343A40", "#2B3035", "#495057", "#000000")),
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme,
                "The source appearance catalogue has no CSS mapping for this theme.")
        };

    private static SourceCssPalette Swap(
        string body, string text, string secondary, string card,
        string border, string onPrimary) =>
        new(text, Blend(text, body, 0.75), Blend(text, body, 0.5),
            secondary, card, border, secondary, onPrimary);

    private static string Blend(string foreground, string background, double opacity)
    {
        var fg = SrgbColor.Parse(foreground);
        var bg = SrgbColor.Parse(background);
        static int Channel(byte front, byte back, double alpha) =>
            (int)Math.Round(front * alpha + back * (1 - alpha));
        return $"#{Channel(fg.R, bg.R, opacity):X2}" +
            $"{Channel(fg.G, bg.G, opacity):X2}" +
            $"{Channel(fg.B, bg.B, opacity):X2}";
    }
}
