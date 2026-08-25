using System.Globalization;

namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// A validated appearance tuple: which theme, which mode, what text size.
/// </summary>
/// <remarks>
/// <para>
/// This is the unit that gets previewed, applied, persisted and reverted. It is one value rather
/// than three loose fields so "set the theme" cannot accidentally drop the mode: every mutation
/// goes through <see cref="WithTheme"/> / <see cref="WithMode"/> / <see cref="WithFontScale"/>,
/// each of which copies the other two forward by construction.
/// </para>
/// <para>
/// <b>This is device- or browser-scoped, never account-scoped.</b> Choosing a dark theme on a
/// phone does not darken the same learner's desktop browser. Persistence therefore lives in device
/// preferences (MAUI) or a per-browser cookie (web), never in the user's server-side profile.
/// </para>
/// <para>
/// The properties are get-only rather than <c>init</c> on purpose: that makes
/// <c>selection with { ThemeId = "not-a-theme" }</c> a compile error, so the validating constructor
/// is the only way in.
/// </para>
/// </remarks>
public sealed record AppearanceSelection
{
    /// <summary>Smallest text scale the settings slider offers.</summary>
    public const double MinFontScale = 0.85;

    /// <summary>Largest text scale the settings slider offers.</summary>
    public const double MaxFontScale = 1.5;

    /// <summary>Text scale before the learner has moved the slider.</summary>
    public const double DefaultFontScale = 1.0;

    /// <summary>
    /// Creates a validated selection.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="themeId"/> is not in <see cref="ThemeCatalog"/>, <paramref name="mode"/> is
    /// not a defined <see cref="ThemeMode"/>, or <paramref name="fontScale"/> is outside
    /// [<see cref="MinFontScale"/>, <see cref="MaxFontScale"/>] or is not a finite number.
    /// </exception>
    public AppearanceSelection(string themeId, ThemeMode mode, double fontScale)
    {
        Theme = ThemeCatalog.Get(themeId);

        if (mode is not (ThemeMode.Light or ThemeMode.Dark))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognized theme mode.");
        }

        if (!IsValidFontScale(fontScale))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fontScale),
                fontScale,
                $"Font scale must be a finite number between {MinFontScale} and {MaxFontScale}.");
        }

        Mode = mode;
        FontScale = fontScale;
    }

    /// <summary>The chosen theme's descriptor. Always a catalogue member.</summary>
    public ThemeDescriptor Theme { get; }

    /// <summary>The chosen theme's stable id — the value written to <c>data-ss-theme</c>.</summary>
    public string ThemeId => Theme.Id;

    /// <summary>Light or dark — the value written to <c>data-bs-theme</c>.</summary>
    public ThemeMode Mode { get; }

    /// <summary>Text scale multiplier, applied as the <c>--ss-font-scale</c> CSS variable.</summary>
    public double FontScale { get; }

    /// <summary>The palette in force for <see cref="Mode"/>.</summary>
    public ThemePalette Palette => Theme.PaletteFor(Mode);

    /// <summary>What a browser or device gets before it has chosen anything.</summary>
    public static AppearanceSelection Default { get; } =
        new(ThemeCatalog.DefaultThemeId, ThemeCatalog.DefaultMode, DefaultFontScale);

    /// <summary>Whether <paramref name="scale"/> is a finite number inside the supported range.</summary>
    public static bool IsValidFontScale(double scale) =>
        double.IsFinite(scale) && scale >= MinFontScale && scale <= MaxFontScale;

    /// <summary>Changes the theme, preserving mode and font scale.</summary>
    public AppearanceSelection WithTheme(string themeId) => new(themeId, Mode, FontScale);

    /// <summary>Changes the mode, preserving theme and font scale.</summary>
    public AppearanceSelection WithMode(ThemeMode mode) => new(ThemeId, mode, FontScale);

    /// <summary>Changes the font scale, preserving theme and mode.</summary>
    public AppearanceSelection WithFontScale(double fontScale) => new(ThemeId, Mode, fontScale);

    // ---------------------------------------------------------------------------------------
    // Bounded token form
    // ---------------------------------------------------------------------------------------

    /// <summary>Schema marker. Bumped only if the token layout changes incompatibly.</summary>
    private const string TokenVersion = "v1";

    private const char TokenSeparator = '.';

    /// <summary>
    /// Hard upper bound on a token this type will parse. The longest legitimate token is well under
    /// 40 characters; the cap exists so a hand-edited or hostile cookie cannot make the parser walk
    /// an arbitrarily long string on every request.
    /// </summary>
    public const int MaxTokenLength = 64;

    /// <summary>
    /// Serializes to the bounded, opaque-to-humans-but-not-secret token stored in the appearance
    /// cookie or in device preferences — for example <c>v1.seoul-pop.dark.100</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not JSON: the value is written into a cookie, and a flat dotted token has no
    /// characters that need cookie-value escaping, no nesting for a parser to recurse into, and a
    /// length that is obvious by inspection. The scale is carried as an integer percentage so the
    /// token never depends on the current culture's decimal separator.
    /// </remarks>
    public string ToToken()
    {
        var scalePercent = (int)Math.Round(FontScale * 100, MidpointRounding.AwayFromZero);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{TokenVersion}{TokenSeparator}{ThemeId}{TokenSeparator}{Mode.ToToken()}{TokenSeparator}{scalePercent}");
    }

    /// <summary>
    /// Parses a token from <b>untrusted</b> input — a cookie the browser handed back, a preference
    /// written by an older build. Returns <see langword="false"/> for anything malformed, over
    /// length, or naming a theme or mode outside the closed sets. It never coerces: the caller
    /// decides to fall back to <see cref="Default"/>, and can log that it happened.
    /// </summary>
    public static bool TryParse(string? token, out AppearanceSelection selection)
    {
        selection = null!;

        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        var parts = token.Split(TokenSeparator);
        if (parts.Length != 4)
        {
            return false;
        }

        if (!string.Equals(parts[0], TokenVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ThemeCatalog.Contains(parts[1]))
        {
            return false;
        }

        if (!ThemeModeExtensions.TryParse(parts[2], out var mode))
        {
            return false;
        }

        if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var scalePercent))
        {
            return false;
        }

        var fontScale = scalePercent / 100.0;
        if (!IsValidFontScale(fontScale))
        {
            return false;
        }

        selection = new AppearanceSelection(parts[1], mode, fontScale);
        return true;
    }

    /// <summary>
    /// Parses a token, falling back to <see cref="Default"/> when it is missing or invalid.
    /// <paramref name="usedFallback"/> reports which happened so a caller can log the rejection.
    /// </summary>
    public static AppearanceSelection ParseOrDefault(string? token, out bool usedFallback)
    {
        if (TryParse(token, out var parsed))
        {
            usedFallback = false;
            return parsed;
        }

        usedFallback = true;
        return Default;
    }
}
