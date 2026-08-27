namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// How a theme's <b>brand palette</b> (primary + accent) responds to the light/dark mode toggle.
/// </summary>
/// <remarks>
/// <para>
/// The two families in the catalogue are implemented differently in CSS, and the difference is
/// visible to the UI, so it is described here rather than being rediscovered by string-matching
/// theme ids at each call site.
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="PaletteFollowsMode"/> — the five custom themes (seoul-pop, ocean, forest, sunset,
/// monochrome). They are CSS-variable overlays on stock Bootstrap, declared as
/// <c>[data-ss-theme="x"][data-bs-theme="light|dark"]</c> pairs in <c>app.css</c>, so the primary
/// and accent hexes genuinely differ between the two modes.
/// </item>
/// <item>
/// <see cref="PaletteFixedAcrossModes"/> — the five Bootswatch themes (flatly, sketchy, slate,
/// vapor, brite). Selecting one swaps the entire Bootstrap stylesheet. Those stylesheets ship
/// surfaces for both modes, so backgrounds still follow <c>data-bs-theme</c>, but the brand
/// palette is baked into the build and is identical in light and dark.
/// </item>
/// </list>
/// </remarks>
public enum ThemeModeBehavior
{
    /// <summary>Primary and accent differ between light and dark.</summary>
    PaletteFollowsMode,

    /// <summary>
    /// Primary and accent are identical in both modes; only surfaces follow the mode.
    /// </summary>
    PaletteFixedAcrossModes
}
