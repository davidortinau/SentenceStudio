using System.Text.RegularExpressions;
using FluentAssertions;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// CSS contract tests for Sam mobile modal layout (Kaylee defects 1-4 implementation).
/// Asserted against the stylesheet text — no layout engine in this suite.
/// Visual confirmation lives in on-device DevFlow checks.
/// </summary>
public class SamMobileModalLayoutContractTests
{
    private static readonly string Css = LoadCss();

    private static string LoadCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        var path = Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("repository root not found"),
            "src", "SentenceStudio.UI", "wwwroot", "css", "app.css");

        return File.ReadAllText(path);
    }

    // ─── B9: Backdrop renders below panel (z-index ordering) ───

    [Fact]
    public void Sam_backdrop_z_index_is_below_panel()
    {
        var backdropZ = ExtractZIndex(".sam-backdrop");
        var panelZ = ExtractZIndex(".sam-panel");

        backdropZ.Should().BeLessThan(panelZ,
            "backdrop must stack below panel so panel remains clickable");
    }

    [Fact]
    public void Sam_backdrop_has_fixed_inset_zero()
    {
        var block = Block(".sam-backdrop");
        block.Should().Contain("position: fixed");
        block.Should().Contain("inset: 0");
    }

    // ─── B10: Desktop has no backdrop (structural — tested via razor conditional) ───
    // The backdrop div only renders when IsMobileModal is true (viewport < 992 && not collapsed).
    // This is a Razor conditional test, confirmed in SamOverlayHostRenderTests.

    // ─── B11: Panel uses grid with 3 rows (one DOM, Compact/Expanded/FullScreen) ───

    [Fact]
    public void Sam_panel_uses_three_row_grid()
    {
        var block = Block(".sam-panel");
        block.Should().Contain("display: grid");
        block.Should().Contain("grid-template-rows: auto minmax(0, 1fr) auto");
    }

    [Fact]
    public void Header_is_grid_row_1()
    {
        var block = Block(".sam-panel__header");
        block.Should().Contain("grid-row: 1");
    }

    [Fact]
    public void Body_is_grid_row_2()
    {
        var block = Block(".sam-panel__body");
        block.Should().Contain("grid-row: 2");
    }

    [Fact]
    public void Composer_is_grid_row_3()
    {
        var block = Block(".sam-panel__composer");
        block.Should().Contain("grid-row: 3");
    }

    // ─── B12: Fullscreen CSS contains all 4 env insets ───

    [Fact]
    public void Fullscreen_has_safe_area_inset_top()
    {
        var block = Block(".sam-panel--fullscreen");
        block.Should().Contain("env(safe-area-inset-top");
    }

    [Fact]
    public void Fullscreen_has_safe_area_inset_right()
    {
        var block = Block(".sam-panel--fullscreen");
        block.Should().Contain("env(safe-area-inset-right");
    }

    [Fact]
    public void Fullscreen_has_safe_area_inset_bottom()
    {
        var block = Block(".sam-panel--fullscreen");
        block.Should().Contain("env(safe-area-inset-bottom");
    }

    [Fact]
    public void Fullscreen_has_safe_area_inset_left()
    {
        var block = Block(".sam-panel--fullscreen");
        block.Should().Contain("env(safe-area-inset-left");
    }

    // ─── B13: Header and composer are non-scrolling; only body scrolls ───

    [Fact]
    public void Header_has_overflow_hidden()
    {
        var block = Block(".sam-panel__header");
        block.Should().Contain("overflow: hidden");
    }

    [Fact]
    public void Composer_has_overflow_hidden()
    {
        var block = Block(".sam-panel__composer");
        block.Should().Contain("overflow: hidden");
    }

    [Fact]
    public void Body_has_overflow_hidden_container()
    {
        // The body itself is overflow:hidden as a flex container;
        // scrolling happens inside .coach-messages within it
        var block = Block(".sam-panel__body");
        block.Should().Contain("overflow: hidden");
    }

    // ─── B14: Mobile page header does not duplicate the app shell's top inset ───

    [Fact]
    public void Mobile_page_header_does_not_apply_safe_area_top_padding()
    {
        // Inside the mobile breakpoint, .page-header must NOT have padding-top: env(safe-area-inset-top)
        // The unconditional app-safe-area-top element is the normal shell's single notch owner.
        var mobileBlock = MobilePageHeaderBlock();
        mobileBlock.Should().NotContain("env(safe-area-inset-top",
            "app-safe-area-top is the normal shell owner — page-header must not duplicate it");
    }

    // ─── B17: No hardcoded color when theme token exists; backdrop uses theme ───

    [Fact]
    public void Sam_panel_background_uses_css_variable()
    {
        var block = Block(".sam-panel");
        block.Should().Contain("var(--bs-body-bg)",
            "panel background must use theme variable, not hardcoded color");
    }

    [Fact]
    public void Sam_panel_border_uses_css_variable()
    {
        var block = Block(".sam-panel");
        block.Should().Contain("var(--bs-border-color)");
    }

    [Fact]
    public void Sam_panel_title_color_uses_css_variable()
    {
        var block = Block(".sam-panel__title");
        block.Should().Contain("var(--bs-body-color)");
    }

    // ─── B17: Backdrop stacking below panel/above app ───

    [Fact]
    public void Backdrop_z_index_is_1045()
    {
        var block = Block(".sam-backdrop");
        block.Should().Contain("z-index: 1045");
    }

    [Fact]
    public void Panel_z_index_is_1050()
    {
        var block = Block(".sam-panel");
        block.Should().Contain("z-index: 1050");
    }

    // ─── Helpers ───

    private static string Block(string selector)
    {
        // Match a rule where the selector is the ENTIRE selector (starts at line beginning)
        var escaped = Regex.Escape(selector);
        var pattern = @"(?<=\n)" + escaped + @"(?:\s*,\s*[^{]+)?\s*\{([^}]*)\}";
        var match = Regex.Match(Css, pattern);

        match.Success.Should().BeTrue($"the stylesheet must define {selector}");
        return match.Groups[1].Value;
    }

    private static int ExtractZIndex(string selector)
    {
        var block = Block(selector);
        var match = Regex.Match(block, @"z-index:\s*(\d+)");
        match.Success.Should().BeTrue($"{selector} must declare z-index");
        return int.Parse(match.Groups[1].Value);
    }

    private static string MobilePageHeaderBlock()
    {
        const string mediaQuery = "@media (max-width: 767.98px)";
        var searchFrom = 0;

        while ((searchFrom = Css.IndexOf(mediaQuery, searchFrom, StringComparison.Ordinal)) >= 0)
        {
            var openingBrace = Css.IndexOf('{', searchFrom + mediaQuery.Length);
            openingBrace.Should().BeGreaterThan(searchFrom);

            var mediaBody = BalancedBlockContents(Css, openingBrace);
            var match = Regex.Match(
                mediaBody,
                @"(?m)^[ \t]*\.page-header\s*\{([^}]*)\}");
            if (match.Success)
                return match.Groups[1].Value;

            searchFrom = openingBrace + mediaBody.Length + 2;
        }

        false.Should().BeTrue(
            "the stylesheet must define .page-header inside @media (max-width: 767.98px)");
        return string.Empty;
    }

    private static string BalancedBlockContents(string source, int openingBrace)
    {
        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[(openingBrace + 1)..index];
                    break;
            }
        }

        throw new InvalidOperationException("Unbalanced CSS block");
    }
}
