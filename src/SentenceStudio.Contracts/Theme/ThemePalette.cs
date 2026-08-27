namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// One theme's colours for one mode, plus the contrast facts derived from them.
/// </summary>
/// <remarks>
/// The hexes mirror the CSS custom properties that actually ship: <c>--bs-primary</c>,
/// <c>--ss-accent</c> and <c>--bs-body-bg</c> from <c>app.css</c> for the custom themes, and the
/// compiled Bootswatch stylesheets for the rest. Anything reading a theme colour — the settings
/// swatch today, a future Coach appearance capability — reads it from here, so there is exactly one
/// place to correct when a hex moves.
/// </remarks>
public sealed record ThemePalette
{
    public ThemePalette(string primary, string accent, string surface)
    {
        Primary = SrgbColor.Parse(primary);
        Accent = SrgbColor.Parse(accent);
        Surface = SrgbColor.Parse(surface);
        PrimaryOnSurface = ThemeContrast.Between(Primary, Surface);
        AccentOnSurface = ThemeContrast.Between(Accent, Surface);
    }

    /// <summary>The theme's brand colour for this mode (<c>--bs-primary</c>).</summary>
    public SrgbColor Primary { get; }

    /// <summary>The theme's secondary emphasis colour for this mode (<c>--ss-accent</c>).</summary>
    public SrgbColor Accent { get; }

    /// <summary>The page background this mode paints behind content (<c>--bs-body-bg</c>).</summary>
    public SrgbColor Surface { get; }

    /// <summary>Contrast of <see cref="Primary"/> against <see cref="Surface"/>.</summary>
    public ThemeContrast PrimaryOnSurface { get; }

    /// <summary>Contrast of <see cref="Accent"/> against <see cref="Surface"/>.</summary>
    public ThemeContrast AccentOnSurface { get; }
}
