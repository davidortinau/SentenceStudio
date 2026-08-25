namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// The complete, closed description of one selectable theme.
/// </summary>
/// <remarks>
/// <para>
/// Everything a caller needs in order to name, describe, preview or apply a theme lives on this
/// record. Before it existed the same ten themes were described in four places — a <c>string[]</c>
/// of ids, a <c>switch</c> of English display names, a <c>switch</c> of preview hexes, and a
/// <c>BOOTSWATCH_THEMES</c> array in JavaScript — and the display-name and colour switches both
/// ended in a silent <c>_ =&gt;</c> fallback, so a typo'd id rendered as itself in blue-and-orange
/// instead of failing.
/// </para>
/// </remarks>
/// <param name="Id">
/// The stable identifier. This is the value written to <c>data-ss-theme</c>, to the appearance
/// cookie and to device preferences, and it is the value a future Coach appearance capability would
/// carry. It never changes once shipped.
/// </param>
/// <param name="LocalizationKey">
/// Resource key for the human-readable name in <c>AppResources.resx</c>. The catalogue deliberately
/// carries a key rather than an English string: the name is user-visible chrome and Korean learners
/// see the Korean resource.
/// </param>
/// <param name="Light">Palette used when the mode is <see cref="ThemeMode.Light"/>.</param>
/// <param name="Dark">Palette used when the mode is <see cref="ThemeMode.Dark"/>.</param>
/// <param name="ModeBehavior">Whether the brand palette follows the mode toggle.</param>
/// <param name="StylesheetSwap">
/// For Bootswatch themes, the stylesheet the browser must load instead of stock Bootstrap. Null for
/// the CSS-variable themes, which overlay stock Bootstrap rather than replacing it.
/// </param>
public sealed record ThemeDescriptor(
    string Id,
    string LocalizationKey,
    ThemePalette Light,
    ThemePalette Dark,
    ThemeModeBehavior ModeBehavior,
    string? StylesheetSwap)
{
    /// <summary>The palette for <paramref name="mode"/>.</summary>
    public ThemePalette PaletteFor(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => Light,
        ThemeMode.Dark => Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognized theme mode.")
    };

    /// <summary>True when selecting this theme replaces the Bootstrap stylesheet entirely.</summary>
    public bool IsStylesheetSwap => StylesheetSwap is not null;
}
