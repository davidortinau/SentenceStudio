using FluentAssertions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The text-size slider's input boundary.
/// </summary>
/// <remarks>
/// <para>
/// The value arrives as a string from the DOM. <c>double.TryParse</c> with
/// <c>NumberStyles.Any</c> accepts <c>"NaN"</c> and <c>"Infinity"</c>, and
/// <c>Math.Clamp(NaN, min, max)</c> returns NaN rather than clamping it — so a parse-then-clamp
/// hands NaN to a setter that throws, inside a Blazor event handler, which ends the learner's
/// circuit over a slider drag.
/// </para>
/// </remarks>
public class AppearanceInputTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("nan")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("∞")]
    [InlineData("-∞")]
    public void Non_finite_values_are_refused_rather_than_clamped(string raw)
    {
        AppearanceInput.TryParseFontScale(raw, out _).Should().BeFalse(
            "Math.Clamp does not clamp NaN, so this must be caught before the clamp");
    }

    [Fact]
    public void A_refused_value_never_reaches_the_appearance_setter()
    {
        // The pairing that matters: what the guard rejects is exactly what the setter would throw on.
        AppearanceInput.TryParseFontScale("NaN", out _).Should().BeFalse();
        AppearanceSelection.IsValidFontScale(double.NaN).Should().BeFalse();

        var act = () => new AppearanceSelection("ocean", ThemeMode.Dark, double.NaN);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("large")]
    [InlineData("1.2.3")]
    public void Unparseable_values_are_refused(string? raw)
    {
        AppearanceInput.TryParseFontScale(raw, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.85", 0.85)]
    [InlineData("1", 1.0)]
    [InlineData("1.25", 1.25)]
    [InlineData("1.5", 1.5)]
    public void In_range_values_pass_through_unchanged(string raw, double expected)
    {
        AppearanceInput.TryParseFontScale(raw, out var scale).Should().BeTrue();
        scale.Should().Be(expected);
    }

    [Theory]
    [InlineData("0.1", AppearanceSelection.MinFontScale)]
    [InlineData("-5", AppearanceSelection.MinFontScale)]
    [InlineData("9", AppearanceSelection.MaxFontScale)]
    [InlineData("1000000", AppearanceSelection.MaxFontScale)]
    public void Out_of_range_but_finite_values_are_clamped(string raw, double expected)
    {
        AppearanceInput.TryParseFontScale(raw, out var scale).Should().BeTrue();
        scale.Should().Be(expected);
    }

    [Fact]
    public void Everything_it_accepts_is_something_the_appearance_setter_accepts()
    {
        // The guard's contract in one line: accept implies constructible.
        foreach (var raw in new[] { "0.85", "1", "1.25", "1.5", "0.1", "9", "-5" })
        {
            AppearanceInput.TryParseFontScale(raw, out var scale).Should().BeTrue();
            AppearanceSelection.IsValidFontScale(scale).Should().BeTrue($"'{raw}' was accepted");

            var act = () => new AppearanceSelection("ocean", ThemeMode.Dark, scale);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Parsing_is_culture_invariant()
    {
        // The browser always sends '.' for a range input regardless of the learner's locale, so a
        // culture-sensitive parse would reject every value on a de-DE machine.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            AppearanceInput.TryParseFontScale("1.25", out var scale).Should().BeTrue();
            scale.Should().Be(1.25);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_settings_page_routes_the_slider_through_the_guard()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.UI")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        var settings = File.ReadAllText(
            Path.Combine(directory!.FullName, "src", "SentenceStudio.UI", "Pages", "Settings.razor"));

        settings.Should().Contain("AppearanceInput.TryParseFontScale",
            "the guard is only useful if the slider actually goes through it");
        settings.Should().NotContain("Math.Clamp(",
            "clamping inline is what let NaN through in the first place");
    }
}
