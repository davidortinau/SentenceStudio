using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;

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

internal sealed class SourceUiAppearance
{
    internal const string PreferenceKey = "AppAppearance";

    private SourceUiAppearance(AppearanceSelection selection)
    {
        Selection = selection;
        LightColors = NativeThemeColorTable.For(selection.ThemeId, dark: false);
        DarkColors = NativeThemeColorTable.For(selection.ThemeId, dark: true);
        Light = CssPalette(LightColors);
        Dark = CssPalette(DarkColors);
    }

    internal AppearanceSelection Selection { get; }
    internal SourceCssPalette Light { get; }
    internal SourceCssPalette Dark { get; }
    // The shared web colour table; the same values the Android head reads.
    private NativeThemeColors LightColors { get; }
    private NativeThemeColors DarkColors { get; }

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
    internal SourceColorPair BorderSecondary => new(LightColors.Secondary, DarkColors.Secondary);
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
            var secondary = BorderSecondary;
            return new(NativeThemeColorTable.Blend(secondary.Light, BodyBackground.Light, 0.3),
                NativeThemeColorTable.Blend(secondary.Dark, BodyBackground.Dark, 0.3));
        }
    }
    internal SourceColorPair Success => new("#12B886", "#12B886");
    internal SourceColorPair Danger => new("#E13B30", "#E13B30");
    /// <summary>Destructive button fill/label. White on #E13B30 is 4.31:1; #B3261E with white is 6.54:1 in both themes.</summary>
    internal SourceColorPair DangerButtonFill => new("#B3261E", "#B3261E");
    internal SourceColorPair DangerButtonText => new("#FFFFFF", "#FFFFFF");
    internal SourceColorPair Warning => new("#FFC233", "#FFC233");
    internal SourceColorPair SuccessAlertFill =>
        new(LightColors.SuccessAlert.Fill, DarkColors.SuccessAlert.Fill);
    internal SourceColorPair SuccessAlertText =>
        new(LightColors.SuccessAlert.Text, DarkColors.SuccessAlert.Text);
    internal SourceColorPair SuccessAlertBorder =>
        new(LightColors.SuccessAlert.Border, DarkColors.SuccessAlert.Border);
    internal SourceColorPair DangerAlertFill =>
        new(LightColors.DangerAlert.Fill, DarkColors.DangerAlert.Fill);
    internal SourceColorPair DangerAlertText =>
        new(LightColors.DangerAlert.Text, DarkColors.DangerAlert.Text);
    internal SourceColorPair DangerAlertBorder =>
        new(LightColors.DangerAlert.Border, DarkColors.DangerAlert.Border);
    internal SourceColorPair InfoAlertFill =>
        new(LightColors.InfoAlert.Fill, DarkColors.InfoAlert.Fill);
    internal SourceColorPair InfoAlertText =>
        new(LightColors.InfoAlert.Text, DarkColors.InfoAlert.Text);
    internal SourceColorPair InfoAlertBorder =>
        new(LightColors.InfoAlert.Border, DarkColors.InfoAlert.Border);
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

    private static SourceCssPalette CssPalette(NativeThemeColors colors) =>
        new(colors.Text, colors.SecondaryText, colors.TertiaryText,
            colors.SecondarySurface, colors.CardSurface, colors.Border,
            colors.ElevatedSurface, colors.OnPrimary);
}
