using FluentAssertions;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The validated appearance tuple, and the bounded token it persists as.
/// </summary>
/// <remarks>
/// The token is what lives in an untrusted place — a browser cookie the learner can hand-edit — so
/// most of these tests are about what it refuses. The rest are about the tuple's central promise:
/// changing one field never drops the other two.
/// </remarks>
public class AppearanceSelectionTests
{
    [Fact]
    public void Default_is_the_catalogue_default_in_dark_at_full_size()
    {
        AppearanceSelection.Default.ThemeId.Should().Be(ThemeCatalog.DefaultThemeId);
        AppearanceSelection.Default.Mode.Should().Be(ThemeCatalog.DefaultMode);
        AppearanceSelection.Default.FontScale.Should().Be(AppearanceSelection.DefaultFontScale);
    }

    // -------------------------------------------------------------------------------------------
    // Field preservation
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void WithTheme_preserves_mode_and_font_scale()
    {
        var start = new AppearanceSelection("ocean", ThemeMode.Light, 1.25);

        var next = start.WithTheme("vapor");

        next.ThemeId.Should().Be("vapor");
        next.Mode.Should().Be(ThemeMode.Light);
        next.FontScale.Should().Be(1.25);
    }

    [Fact]
    public void WithMode_preserves_theme_and_font_scale()
    {
        var start = new AppearanceSelection("ocean", ThemeMode.Light, 1.25);

        var next = start.WithMode(ThemeMode.Dark);

        next.ThemeId.Should().Be("ocean");
        next.Mode.Should().Be(ThemeMode.Dark);
        next.FontScale.Should().Be(1.25);
    }

    [Fact]
    public void WithFontScale_preserves_theme_and_mode()
    {
        var start = new AppearanceSelection("ocean", ThemeMode.Light, 1.0);

        var next = start.WithFontScale(0.85);

        next.ThemeId.Should().Be("ocean");
        next.Mode.Should().Be(ThemeMode.Light);
        next.FontScale.Should().Be(0.85);
    }

    // -------------------------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Constructing_with_an_unknown_theme_throws_rather_than_falling_back()
    {
        var act = () => new AppearanceSelection("not-a-theme", ThemeMode.Dark, 1.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.84)]
    [InlineData(1.51)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructing_with_an_out_of_range_font_scale_throws(double scale)
    {
        var act = () => new AppearanceSelection("ocean", ThemeMode.Dark, scale);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void The_slider_endpoints_are_valid(double scale)
    {
        AppearanceSelection.IsValidFontScale(scale).Should().BeTrue();
    }

    [Fact]
    public void Constructing_with_an_undefined_mode_throws()
    {
        var act = () => new AppearanceSelection("ocean", (ThemeMode)42, 1.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------------------------
    // Token round trip
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Token_round_trips_every_theme_in_every_mode()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                var original = new AppearanceSelection(theme.Id, mode, 1.15);

                AppearanceSelection.TryParse(original.ToToken(), out var parsed).Should().BeTrue();
                parsed.Should().Be(original);
            }
        }
    }

    [Fact]
    public void Token_has_a_stable_shape()
    {
        new AppearanceSelection("seoul-pop", ThemeMode.Dark, 1.0).ToToken().Should().Be("v1.seoul-pop.dark.100");
        new AppearanceSelection("vapor", ThemeMode.Light, 0.85).ToToken().Should().Be("v1.vapor.light.85");
        new AppearanceSelection("brite", ThemeMode.Light, 1.5).ToToken().Should().Be("v1.brite.light.150");
    }

    [Fact]
    public void Token_is_culture_invariant()
    {
        // A comma decimal separator in a cookie would round-trip on the author's machine and fail
        // on a learner's. The scale is carried as an integer percentage precisely to avoid that.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var token = new AppearanceSelection("ocean", ThemeMode.Dark, 1.25).ToToken();

            token.Should().Be("v1.ocean.dark.125");
            AppearanceSelection.TryParse(token, out var parsed).Should().BeTrue();
            parsed.FontScale.Should().Be(1.25);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Token_stays_well_inside_the_length_cap()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            new AppearanceSelection(theme.Id, ThemeMode.Light, 1.5)
                .ToToken().Length.Should().BeLessThan(AppearanceSelection.MaxTokenLength);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Rejection of untrusted input
    // -------------------------------------------------------------------------------------------

    public static TheoryData<string?> InvalidTokens() =>
    [
        null,
        "",
        "   ",
        "garbage",
        "v1.seoul-pop.dark",                 // too few parts
        "v1.seoul-pop.dark.100.extra",       // too many parts
        "v2.seoul-pop.dark.100",             // unknown schema version
        "v1.not-a-theme.dark.100",           // theme outside the catalogue
        "v1.seoul-pop.sepia.100",            // mode outside the closed set
        "v1.seoul-pop.dark.84",              // below the supported range
        "v1.seoul-pop.dark.151",             // above the supported range
        "v1.seoul-pop.dark.-100",            // negative
        "v1.seoul-pop.dark.1e2",             // not a plain integer
        "v1.seoul-pop.dark.100 ",            // trailing whitespace
        "v1.SEOUL-POP.dark.100"              // ids are ordinal, not case-insensitive
    ];

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public void Invalid_tokens_are_rejected(string? token)
    {
        AppearanceSelection.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void An_over_length_token_is_rejected_without_being_parsed()
    {
        var oversized = "v1.seoul-pop.dark.100" + new string('x', AppearanceSelection.MaxTokenLength);

        oversized.Length.Should().BeGreaterThan(AppearanceSelection.MaxTokenLength);
        AppearanceSelection.TryParse(oversized, out _).Should().BeFalse();
    }

    [Fact]
    public void ParseOrDefault_reports_when_it_fell_back()
    {
        AppearanceSelection.ParseOrDefault("v1.ocean.light.110", out var usedFallbackOnValid)
            .ThemeId.Should().Be("ocean");
        usedFallbackOnValid.Should().BeFalse();

        AppearanceSelection.ParseOrDefault("v1.not-a-theme.light.110", out var usedFallbackOnInvalid)
            .Should().Be(AppearanceSelection.Default);
        usedFallbackOnInvalid.Should().BeTrue();
    }

    // -------------------------------------------------------------------------------------------
    // Mode tokens
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("light", ThemeMode.Light)]
    [InlineData("dark", ThemeMode.Dark)]
    [InlineData("LIGHT", ThemeMode.Light)]
    [InlineData("Dark", ThemeMode.Dark)]
    public void Mode_tokens_parse_case_insensitively(string token, ThemeMode expected)
    {
        ThemeModeExtensions.TryParse(token, out var mode).Should().BeTrue();
        mode.Should().Be(expected);
    }

    [Theory]
    [InlineData("sepia")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("system")]
    public void Unknown_mode_tokens_are_rejected(string? token)
    {
        ThemeModeExtensions.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void The_mode_enum_stays_a_closed_pair()
    {
        // Guards the documented decision not to add an Unknown member until there is a wire DTO
        // that needs one. If a third value appears, every mode switch in the UI needs revisiting.
        Enum.GetValues<ThemeMode>().Should().Equal(ThemeMode.Light, ThemeMode.Dark);
    }
}
