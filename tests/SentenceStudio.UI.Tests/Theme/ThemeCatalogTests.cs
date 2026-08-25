using FluentAssertions;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The theme catalogue is a closed set, and every entry has to be complete enough to render.
/// </summary>
/// <remarks>
/// <para>
/// Before the catalogue existed, the ten themes were described in four separate places — an id
/// array, a display-name <c>switch</c>, a preview-colour <c>switch</c>, and a
/// <c>BOOTSWATCH_THEMES</c> array in JavaScript. Two of those ended in a silent <c>_ =&gt;</c>
/// fallback, so adding an eleventh theme to the array and forgetting the colours produced a
/// swatch painted in Seoul Pop's blue and orange rather than a failure. These tests are the
/// replacement for that missing failure.
/// </para>
/// </remarks>
public class ThemeCatalogTests
{
    /// <summary>
    /// The exact ten, in picker order. A snapshot: adding, removing or reordering a theme is a
    /// product decision and should require deliberately editing this list.
    /// </summary>
    private static readonly string[] ExpectedIds =
    [
        "seoul-pop", "ocean", "forest", "sunset", "monochrome",
        "flatly", "sketchy", "slate", "vapor", "brite"
    ];

    [Fact]
    public void Catalog_contains_exactly_the_expected_themes_in_picker_order()
    {
        ThemeCatalog.All.Select(t => t.Id).Should().Equal(ExpectedIds);
    }

    [Fact]
    public void Theme_ids_are_unique()
    {
        ThemeCatalog.All.Select(t => t.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_theme_has_a_localization_key_rather_than_a_hardcoded_english_name()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            theme.LocalizationKey.Should().NotBeNullOrWhiteSpace(
                "every theme name is user-visible chrome and Korean learners read the Korean resource");
            theme.LocalizationKey.Should().StartWith(
                "Theme_",
                "the resource keys are namespaced so a missing one is obvious in the resx");
        }
    }

    [Fact]
    public void Every_theme_has_localization_keys_that_are_unique()
    {
        ThemeCatalog.All.Select(t => t.LocalizationKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_theme_has_a_light_and_a_dark_palette()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                var palette = theme.PaletteFor(mode);

                palette.Should().NotBeNull($"{theme.Id} must render in {mode}");
                palette.Primary.Hex.Should().MatchRegex("^#[0-9A-F]{6}$");
                palette.Accent.Hex.Should().MatchRegex("^#[0-9A-F]{6}$");
                palette.Surface.Hex.Should().MatchRegex("^#[0-9A-F]{6}$");
            }
        }
    }

    [Fact]
    public void Every_palette_carries_computed_contrast_metadata()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                var palette = theme.PaletteFor(mode);

                // Derived from the same hexes the CSS uses, so it cannot drift away from them.
                palette.PrimaryOnSurface.Ratio.Should().BeInRange(1.0, 21.0);
                palette.AccentOnSurface.Ratio.Should().BeInRange(1.0, 21.0);
                palette.PrimaryOnSurface.Foreground.Should().Be(palette.Primary);
                palette.PrimaryOnSurface.Background.Should().Be(palette.Surface);
                palette.AccentOnSurface.Foreground.Should().Be(palette.Accent);
            }
        }
    }

    [Fact]
    public void Every_palette_names_a_readable_text_colour_for_its_primary()
    {
        // The swatch prints a theme's name; this is what keeps that name legible without relying on
        // colour alone to identify the theme.
        foreach (var theme in ThemeCatalog.All)
        {
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                var contrast = theme.PaletteFor(mode).PrimaryOnSurface;
                var readable = contrast.ReadableTextOnForeground;

                readable.Should().BeOneOf("#FFFFFF", "#000000");

                var chosen = SrgbColor.Parse(readable);
                chosen.ContrastRatio(theme.PaletteFor(mode).Primary).Should().BeGreaterThanOrEqualTo(
                    4.5,
                    $"{theme.Id}/{mode} must be able to print legible text on its own primary");
            }
        }
    }

    [Theory]
    [InlineData("seoul-pop", ThemeModeBehavior.PaletteFollowsMode)]
    [InlineData("ocean", ThemeModeBehavior.PaletteFollowsMode)]
    [InlineData("forest", ThemeModeBehavior.PaletteFollowsMode)]
    [InlineData("sunset", ThemeModeBehavior.PaletteFollowsMode)]
    [InlineData("monochrome", ThemeModeBehavior.PaletteFollowsMode)]
    [InlineData("flatly", ThemeModeBehavior.PaletteFixedAcrossModes)]
    [InlineData("sketchy", ThemeModeBehavior.PaletteFixedAcrossModes)]
    [InlineData("slate", ThemeModeBehavior.PaletteFixedAcrossModes)]
    [InlineData("vapor", ThemeModeBehavior.PaletteFixedAcrossModes)]
    [InlineData("brite", ThemeModeBehavior.PaletteFixedAcrossModes)]
    public void Mode_behavior_matches_how_the_theme_is_implemented_in_css(
        string id,
        ThemeModeBehavior expected)
    {
        ThemeCatalog.Get(id).ModeBehavior.Should().Be(expected);
    }

    [Fact]
    public void Bootswatch_themes_swap_a_stylesheet_and_custom_themes_do_not()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            if (theme.ModeBehavior == ThemeModeBehavior.PaletteFixedAcrossModes)
            {
                theme.StylesheetSwap.Should().Be(
                    theme.Id,
                    "the Bootswatch stylesheet is named after the theme");
                theme.IsStylesheetSwap.Should().BeTrue();
            }
            else
            {
                theme.StylesheetSwap.Should().BeNull(
                    "CSS-variable themes overlay stock Bootstrap rather than replacing it");
                theme.IsStylesheetSwap.Should().BeFalse();
            }
        }
    }

    [Fact]
    public void Fixed_palette_themes_really_do_use_the_same_brand_colours_in_both_modes()
    {
        foreach (var theme in ThemeCatalog.All.Where(t =>
                     t.ModeBehavior == ThemeModeBehavior.PaletteFixedAcrossModes))
        {
            theme.Light.Primary.Should().Be(theme.Dark.Primary, $"{theme.Id} is a stylesheet swap");
            theme.Light.Accent.Should().Be(theme.Dark.Accent, $"{theme.Id} is a stylesheet swap");
        }
    }

    [Fact]
    public void Mode_following_themes_really_do_change_brand_colours_between_modes()
    {
        foreach (var theme in ThemeCatalog.All.Where(t =>
                     t.ModeBehavior == ThemeModeBehavior.PaletteFollowsMode))
        {
            theme.Light.Primary.Should().NotBe(
                theme.Dark.Primary,
                $"{theme.Id} declares light and dark primaries separately in app.css");
        }
    }

    [Fact]
    public void Unknown_ids_are_rejected_rather_than_silently_falling_back()
    {
        // The old GetThemeColors ended in `_ => ("#6B8CFF", "#FF7A4D")`, so a typo rendered as
        // Seoul Pop's dark palette and looked plausible.
        ThemeCatalog.TryGet("not-a-theme", out _).Should().BeFalse();
        ThemeCatalog.TryGet(null, out _).Should().BeFalse();
        ThemeCatalog.TryGet(string.Empty, out _).Should().BeFalse();
        ThemeCatalog.Contains("Seoul-Pop").Should().BeFalse("ids are compared ordinally, not loosely");

        var act = () => ThemeCatalog.Get("not-a-theme");
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*not-a-theme*")
            .Which.Message.Should().Contain("seoul-pop", "the error names the ids that do exist");
    }

    [Fact]
    public void Default_is_a_catalogue_member()
    {
        ThemeCatalog.Contains(ThemeCatalog.DefaultThemeId).Should().BeTrue();
        ThemeCatalog.Default.Id.Should().Be(ThemeCatalog.DefaultThemeId);
    }
}
