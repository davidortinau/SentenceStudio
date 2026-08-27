using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The theme picker as a learner meets it: named in their language, and never identified by colour
/// alone.
/// </summary>
/// <remarks>
/// <para>
/// The old picker rendered two colour rectangles and put the theme's name in a <c>title</c>
/// attribute — invisible on touch, unreliable to screen readers. That makes colour the sole means
/// of conveying which choice is which (WCAG 1.4.1), and it is worst exactly where it matters most:
/// Monochrome and Slate are both grey.
/// </para>
/// <para>
/// The markup assertions read the <c>.razor</c> source rather than rendering the component, because
/// <c>ThemeSwatch</c> injects the localization service and the point being defended is structural —
/// which element carries the name, which element is hidden from assistive technology — not the
/// rendered text.
/// </para>
/// </remarks>
public class ThemeSwatchPresentationTests
{
    private static string ReadUiSource(string relativePath)
    {
        // tests/SentenceStudio.UI.Tests/bin/<cfg>/<tfm>/ -> repo root
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.UI")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the UI project must be locatable from the test output directory");

        var path = Path.Combine(directory!.FullName, "src", "SentenceStudio.UI", relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} should exist");
        return File.ReadAllText(path);
    }

    // -------------------------------------------------------------------------------------------
    // One shared component
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_swatch_exists_as_one_shared_component()
    {
        ReadUiSource(Path.Combine("Components", "ThemeSwatch.razor"))
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Settings_renders_the_shared_component_rather_than_inlining_swatch_markup()
    {
        var settings = ReadUiSource(Path.Combine("Pages", "Settings.razor"));

        settings.Should().Contain("<ThemeSwatch",
            "the picker is one component so a second picker cannot drift from it");
        settings.Should().NotContain("theme-swatch-colors",
            "the colour strip markup belongs to the component, not to the page");
        settings.Should().NotContain("theme-swatch-label",
            "the label markup belongs to the component, not to the page");
    }

    [Fact]
    public void Settings_drives_the_picker_from_the_catalogue_not_a_parallel_list()
    {
        var settings = ReadUiSource(Path.Combine("Pages", "Settings.razor"));

        settings.Should().Contain("ThemeCatalog.All",
            "one list of themes, so a new theme cannot appear in the picker without a descriptor");
        settings.Should().NotContain("GetThemeColors",
            "preview colours come from the descriptor, which has no silent fallback");
        settings.Should().NotContain("GetThemeDisplayName",
            "names come from the descriptor's localization key");
    }

    // -------------------------------------------------------------------------------------------
    // Accessibility
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_theme_name_is_rendered_as_visible_text()
    {
        var swatch = ReadUiSource(Path.Combine("Components", "ThemeSwatch.razor"));

        swatch.Should().Contain("@DisplayName",
            "the name is visible content, not a tooltip a touch user never sees");
        swatch.Should().MatchRegex(
            new Regex(@"theme-swatch-label[\s\S]*?@DisplayName", RegexOptions.None),
            "the visible label element carries the name");
    }

    [Fact]
    public void The_swatch_never_identifies_a_theme_by_colour_alone()
    {
        var swatch = ReadUiSource(Path.Combine("Components", "ThemeSwatch.razor"));

        // The colour strip is decoration: a screen reader announcing "two coloured boxes" adds
        // nothing, and a learner with a colour vision deficiency cannot use it to choose.
        swatch.Should().Contain(@"class=""theme-swatch-colors"" aria-hidden=""true""");
        swatch.Should().NotContain("title=\"@DisplayName\"",
            "the old picker relied on a title attribute for the name");
    }

    [Fact]
    public void Selection_is_announced_and_shown_by_more_than_a_border_colour()
    {
        var swatch = ReadUiSource(Path.Combine("Components", "ThemeSwatch.razor"));

        swatch.Should().Contain("aria-checked", "assistive technology needs the selected state");
        swatch.Should().Contain(@"role=""radio""", "the swatches are one mutually exclusive choice");
        swatch.Should().Contain("bi-check-circle-fill",
            "selection is also visible without perceiving the accent border colour");
    }

    [Fact]
    public void The_picker_is_grouped_so_a_screen_reader_announces_it_as_one_choice()
    {
        var settings = ReadUiSource(Path.Combine("Pages", "Settings.razor"));

        settings.Should().Contain(@"role=""radiogroup""");
        settings.Should().Contain("aria-labelledby=\"theme-picker-label\"");
    }

    [Fact]
    public void The_swatch_contains_no_emoji()
    {
        // House rule: Bootstrap icons or plain text, never emoji, in any user-facing surface.
        var swatch = ReadUiSource(Path.Combine("Components", "ThemeSwatch.razor"));

        var emoji = swatch.EnumerateRunes()
            .Where(r => r.Value is (>= 0x1F300 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or 0xFE0F)
            .Select(r => r.ToString())
            .ToList();

        emoji.Should().BeEmpty("the swatch uses Bootstrap icons, not emoji");
    }

    // -------------------------------------------------------------------------------------------
    // Localization coverage
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_theme_has_an_english_name_in_the_resources()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var value = LookupResource(theme.LocalizationKey, new CultureInfo("en"));

            value.Should().NotBe(
                theme.LocalizationKey,
                $"{theme.Id} must resolve '{theme.LocalizationKey}' — LocalizationManager returns the key itself when it is missing");
            value.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Every_theme_has_a_korean_name_in_the_resources()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var value = LookupResource(theme.LocalizationKey, new CultureInfo("ko"));

            value.Should().NotBe(
                theme.LocalizationKey,
                $"{theme.Id} must resolve '{theme.LocalizationKey}' in Korean");
            value.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Theme_names_are_unique_within_a_culture_so_two_swatches_cannot_read_alike()
    {
        foreach (var culture in new[] { new CultureInfo("en"), new CultureInfo("ko") })
        {
            var names = ThemeCatalog.All
                .Select(t => LookupResource(t.LocalizationKey, culture))
                .ToList();

            names.Should().OnlyHaveUniqueItems(
                $"a learner reading {culture.Name} must be able to tell every swatch apart by name");
        }
    }

    /// <summary>
    /// Reads a string the same way <c>LocalizationManager.GetString</c> does — including its
    /// "return the key when it is missing" behaviour, which is what these tests detect.
    /// </summary>
    private static string LookupResource(string key, CultureInfo culture)
    {
        var manager = typeof(SentenceStudio.LocalizationManager).Assembly
            .GetType("SentenceStudio.Resources.Strings.AppResources")
            !.GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            !.GetValue(null) as System.Resources.ResourceManager;

        manager.Should().NotBeNull("AppResources.ResourceManager must be reachable");
        return manager!.GetString(key, culture) ?? key;
    }
}
