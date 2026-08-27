using System.Collections.ObjectModel;

namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// The closed set of themes the app offers. Ten entries, fixed at compile time.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no silent fallback.</b> <see cref="Get"/> throws for an id that is not in the
/// catalogue and <see cref="TryGet"/> reports failure, because the two behaviours an unknown id can
/// have — "render it as if it were the default" and "reject it" — belong to different callers.
/// Untrusted input (a hand-edited cookie, a stale preference written by an older build) is parsed
/// through <see cref="AppearanceSelection.TryParse"/>, which rejects and lets the caller choose
/// <see cref="AppearanceSelection.Default"/> explicitly. Trusted input (a swatch the user just
/// clicked, in a list this catalogue produced) throws, because an unknown id there is a bug.
/// </para>
/// <para>
/// Ordering is the order the settings picker renders: the five custom CSS-variable themes first,
/// then the five Bootswatch stylesheet swaps.
/// </para>
/// </remarks>
public static class ThemeCatalog
{
    /// <summary>The theme a browser or device gets before it has ever chosen one.</summary>
    public const string DefaultThemeId = "seoul-pop";

    /// <summary>The mode a browser or device gets before it has ever chosen one.</summary>
    public const ThemeMode DefaultMode = ThemeMode.Dark;

    private static readonly ThemeDescriptor[] _all =
    [
        // ---- Custom CSS-variable themes: overlay stock Bootstrap, palette follows the mode ----
        new(
            Id: "seoul-pop",
            LocalizationKey: "Theme_SeoulPop",
            Light: new ThemePalette(primary: "#1E4DFF", accent: "#FF6A3D", surface: "#F7F8FF"),
            Dark: new ThemePalette(primary: "#6B8CFF", accent: "#FF7A4D", surface: "#060913"),
            ModeBehavior: ThemeModeBehavior.PaletteFollowsMode,
            StylesheetSwap: null),
        new(
            Id: "ocean",
            LocalizationKey: "Theme_Ocean",
            Light: new ThemePalette(primary: "#0891B2", accent: "#14B8A6", surface: "#F0FDFA"),
            Dark: new ThemePalette(primary: "#22D3EE", accent: "#5EEAD4", surface: "#0C1821"),
            ModeBehavior: ThemeModeBehavior.PaletteFollowsMode,
            StylesheetSwap: null),
        new(
            Id: "forest",
            LocalizationKey: "Theme_Forest",
            Light: new ThemePalette(primary: "#059669", accent: "#FBBF24", surface: "#F0FDF4"),
            Dark: new ThemePalette(primary: "#34D399", accent: "#FDE047", surface: "#0F1C13"),
            ModeBehavior: ThemeModeBehavior.PaletteFollowsMode,
            StylesheetSwap: null),
        new(
            Id: "sunset",
            LocalizationKey: "Theme_Sunset",
            Light: new ThemePalette(primary: "#EA580C", accent: "#F472B6", surface: "#FFF7ED"),
            Dark: new ThemePalette(primary: "#FB923C", accent: "#FBA7D8", surface: "#1C1310"),
            ModeBehavior: ThemeModeBehavior.PaletteFollowsMode,
            StylesheetSwap: null),
        new(
            Id: "monochrome",
            LocalizationKey: "Theme_Monochrome",
            Light: new ThemePalette(primary: "#374151", accent: "#1F2937", surface: "#FFFFFF"),
            Dark: new ThemePalette(primary: "#D1D5DB", accent: "#F3F4F6", surface: "#0A0A0A"),
            ModeBehavior: ThemeModeBehavior.PaletteFollowsMode,
            StylesheetSwap: null),

        // ---- Bootswatch themes: swap the whole stylesheet, brand palette fixed across modes ----
        new(
            Id: "flatly",
            LocalizationKey: "Theme_Flatly",
            Light: new ThemePalette(primary: "#2C3E50", accent: "#18BC9C", surface: "#FFFFFF"),
            Dark: new ThemePalette(primary: "#2C3E50", accent: "#18BC9C", surface: "#212529"),
            ModeBehavior: ThemeModeBehavior.PaletteFixedAcrossModes,
            StylesheetSwap: "flatly"),
        new(
            Id: "sketchy",
            LocalizationKey: "Theme_Sketchy",
            Light: new ThemePalette(primary: "#333333", accent: "#868E96", surface: "#FFFFFF"),
            Dark: new ThemePalette(primary: "#333333", accent: "#868E96", surface: "#212529"),
            ModeBehavior: ThemeModeBehavior.PaletteFixedAcrossModes,
            StylesheetSwap: "sketchy"),
        new(
            Id: "slate",
            LocalizationKey: "Theme_Slate",
            Light: new ThemePalette(primary: "#3A3F44", accent: "#7A8288", surface: "#272B30"),
            Dark: new ThemePalette(primary: "#3A3F44", accent: "#7A8288", surface: "#272B30"),
            ModeBehavior: ThemeModeBehavior.PaletteFixedAcrossModes,
            StylesheetSwap: "slate"),
        new(
            Id: "vapor",
            LocalizationKey: "Theme_Vapor",
            Light: new ThemePalette(primary: "#6F42C1", accent: "#EA39B8", surface: "#1A0933"),
            Dark: new ThemePalette(primary: "#6F42C1", accent: "#EA39B8", surface: "#170229"),
            ModeBehavior: ThemeModeBehavior.PaletteFixedAcrossModes,
            StylesheetSwap: "vapor"),
        new(
            Id: "brite",
            LocalizationKey: "Theme_Brite",
            Light: new ThemePalette(primary: "#A2E436", accent: "#FF7518", surface: "#FFFFFF"),
            Dark: new ThemePalette(primary: "#A2E436", accent: "#FF7518", surface: "#212529"),
            ModeBehavior: ThemeModeBehavior.PaletteFixedAcrossModes,
            StylesheetSwap: "brite")
    ];

    private static readonly Dictionary<string, ThemeDescriptor> _byId =
        _all.ToDictionary(t => t.Id, StringComparer.Ordinal);

    /// <summary>Every theme, in picker order.</summary>
    public static IReadOnlyList<ThemeDescriptor> All { get; } = new ReadOnlyCollection<ThemeDescriptor>(_all);

    /// <summary>The descriptor selected when nothing has been chosen.</summary>
    public static ThemeDescriptor Default => _byId[DefaultThemeId];

    /// <summary>
    /// Looks up a theme by id. Use for untrusted ids; the caller decides what an unknown id means.
    /// </summary>
    public static bool TryGet(string? id, out ThemeDescriptor descriptor)
    {
        if (id is not null && _byId.TryGetValue(id, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    /// <summary>
    /// Looks up a theme by id, throwing when it is not in the catalogue. Use for ids that came from
    /// this catalogue in the first place — an unknown one there is a bug, not a user input problem.
    /// </summary>
    public static ThemeDescriptor Get(string? id) =>
        TryGet(id, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(
                nameof(id),
                id,
                $"'{id}' is not a theme in the catalogue. Known ids: {string.Join(", ", _byId.Keys)}.");

    /// <summary>Whether <paramref name="id"/> names a theme in the catalogue.</summary>
    public static bool Contains(string? id) => TryGet(id, out _);
}
