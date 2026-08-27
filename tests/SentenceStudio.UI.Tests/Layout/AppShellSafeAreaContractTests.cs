using System.Text.RegularExpressions;
using FluentAssertions;

namespace SentenceStudio.UI.Tests.Layout;

public class AppShellSafeAreaContractTests
{
    private static readonly string Root = RepoRoot();
    private static readonly string Css = File.ReadAllText(Path.Combine(
        Root, "src", "SentenceStudio.UI", "wwwroot", "css", "app.css"));
    private static readonly string Layout = File.ReadAllText(Path.Combine(
        Root, "src", "SentenceStudio.UI", "Layout", "MainLayout.razor"));

    [Fact]
    public void Shell_renders_the_top_safe_area_before_optional_environment_content()
    {
        var spacer = Layout.IndexOf(
            "<div class=\"app-safe-area-top\" aria-hidden=\"true\"></div>",
            StringComparison.Ordinal);
        var environment = Layout.IndexOf("<EnvironmentBadge />", StringComparison.Ordinal);

        spacer.Should().BeGreaterThanOrEqualTo(0);
        spacer.Should().BeLessThan(environment,
            "the unconditional safe-area owner must touch the top edge even when production hides the environment bar");
    }

    [Fact]
    public void Shell_spacer_tracks_the_dynamic_top_inset()
    {
        var block = CssBlock(".app-safe-area-top");

        block.Should().Contain("height: env(safe-area-inset-top, 0px)");
        block.Should().Contain("flex: 0 0 env(safe-area-inset-top, 0px)",
            "the flex column must not shrink the system inset when the return-to-app bar expands");
    }

    [Fact]
    public void Safe_area_test_seam_defaults_to_the_native_environment()
    {
        var block = CssBlock(":root", rule =>
            rule.Contains("--safe-area-top", StringComparison.Ordinal));

        block.Should().Contain("--safe-area-top: env(safe-area-inset-top, 0px)");
        block.Should().Contain("--safe-area-bottom: env(safe-area-inset-bottom, 0px)");
    }

    [Fact]
    public void Shell_content_tracks_both_dynamic_landscape_insets()
    {
        Layout.Should().Contain(
            "d-flex flex-row flex-grow-1 overflow-hidden app-safe-area-inline",
            "the shell row that contains navigation and page content must own the inline safe area");

        var block = CssBlock(".app-safe-area-inline");
        block.Should().Contain("padding-left: env(safe-area-inset-left, 0px)");
        block.Should().Contain("padding-right: env(safe-area-inset-right, 0px)");
    }

    [Fact]
    public void Fixed_offcanvas_surfaces_own_both_dynamic_landscape_insets()
    {
        var block = CssBlock(".offcanvas");

        block.Should().Contain("padding-left: env(safe-area-inset-left, 0px)");
        block.Should().Contain("padding-right: env(safe-area-inset-right, 0px)");
    }

    [Fact]
    public void Bottom_offcanvas_bodies_add_the_dynamic_bottom_inset_to_bootstrap_spacing()
    {
        var block = CssBlock(".offcanvas-bottom .offcanvas-body");

        block.Should().Contain(
            "padding-bottom: calc(1rem + env(safe-area-inset-bottom, 0px))",
            "bottom sheets must retain Bootstrap's visual padding outside the home-indicator inset");
    }

    [Fact]
    public void Full_height_mobile_navigation_owns_the_dynamic_bottom_inset()
    {
        var block = CssBlock("#mobileNav .offcanvas-body");

        block.Should().Contain(
            "padding-bottom: env(safe-area-inset-bottom, 0px) !important",
            "the p-0 navigation body still needs system spacing at the physical bottom edge");
    }

    [Fact]
    public void Fixed_toast_container_owns_dynamic_bottom_and_right_insets()
    {
        var block = CssBlock(".toast-container-ss");

        block.Should().Contain("bottom: calc(1rem + env(safe-area-inset-bottom, 0px))");
        block.Should().Contain("right: calc(1rem + env(safe-area-inset-right, 0px))");
    }

    [Fact]
    public void Coach_open_toast_offset_keeps_the_dynamic_bottom_inset()
    {
        var block = CssBlock("body.coach-open .toast-container-ss");

        block.Should().Contain("var(--coach-composer-min-h, 76px)");
        block.Should().Contain("env(safe-area-inset-bottom, 0px)",
            "lifting the toast above the composer must not replace its home-indicator ownership");
    }

    [Fact]
    public void Conditional_environment_bar_does_not_own_the_top_inset()
    {
        CssBlock(".env-bar").Should().NotContain("safe-area-inset-top");
        Css.Should().NotMatchRegex(
            @"(?s)@supports[^{]*safe-area-inset-top[^}]*\.env-bar",
            "production does not render the environment bar, so safe-area ownership cannot depend on it");
    }

    [Fact]
    public void Document_and_scrolling_body_are_not_double_padded()
    {
        CssBlock("html, body").Should().NotContain("safe-area-inset-top");
        CssBlock("main.main-content").Should().NotContain("safe-area-inset-top");
    }

    [Fact]
    public void Reading_header_keeps_visual_padding_without_duplicating_the_shell_top_inset()
    {
        var rules = CssBlocks(".reading-page__header .page-header");

        rules.Should().NotBeEmpty();
        foreach (var rule in rules)
        {
            rule.Should().NotContain("safe-area-inset-top",
                "the app shell owns the top inset for every Reading header rule");
        }

        var mobileRule = rules.Single(rule =>
            rule.Contains("position: static", StringComparison.Ordinal));
        mobileRule.Should().Contain("padding: 0 0.75rem",
            "the mobile Reading header still needs horizontal visual padding");
        mobileRule.Should().NotContain("padding-top",
            "a top-padding override would replace the zero vertical padding");
    }

    [Fact]
    public void Coach_page_and_nested_canvas_headers_do_not_duplicate_the_shell_top_inset()
    {
        foreach (var rule in CssBlocks(".coach-header"))
        {
            rule.Should().NotContain("safe-area-inset-top",
                "the generic selector also reaches the /coach canvas header, which is inside the shell");
        }

        Css.Should().NotMatchRegex(
            @"(?ms)^[^{\r\n]*\.coach-page[^{\r\n]*\.coach-header\s*\{[^}]*safe-area-inset-top",
            "/coach is normal shell content and must not own a second top inset");
    }

    [Fact]
    public void Legacy_fullscreen_coach_root_owns_its_independent_safe_area()
    {
        var workspace = CssBlock(".modal.coach-modal .coach-workspace");
        workspace.Should().Contain("padding-left: env(safe-area-inset-left, 0px)");
        workspace.Should().Contain("padding-right: env(safe-area-inset-right, 0px)");

        var rootHeader = CssBlock(
            ".modal.coach-modal .coach-workspace > .coach-workspace-inner > .coach-header");
        rootHeader.Should().Contain(
            "padding-top: calc(0.5rem + env(safe-area-inset-top, 0px))");
    }

    [Fact]
    public void Tablet_legacy_coach_centers_inside_dynamic_top_and_bottom_insets()
    {
        var tablet = CssMediaBlock(
            "@media (min-width: 768px) and (max-width: 991.98px)");
        var modal = CssBlock(
            tablet,
            ".modal.coach-modal",
            rule => rule.Contains("--coach-safe-top", StringComparison.Ordinal));
        var dialog = CssBlock(
            tablet,
            ".modal.coach-modal .modal-dialog.coach-dialog");

        modal.Should().Contain("--coach-safe-top: var(--safe-area-top)");
        modal.Should().Contain("--coach-safe-bottom: var(--safe-area-bottom)");
        modal.Should().Contain(
            "--coach-outer-top: max(1.75rem, var(--coach-safe-top))");
        modal.Should().Contain(
            "--coach-outer-bottom: max(1.75rem, var(--coach-safe-bottom))");
        modal.Should().Contain(
            "--coach-height: min(92dvh, calc(100dvh - var(--coach-outer-top) - var(--coach-outer-bottom)))");

        dialog.Should().Contain("margin-top: var(--coach-outer-top)");
        dialog.Should().Contain("margin-bottom: var(--coach-outer-bottom)");
        dialog.Should().Contain(
            "min-height: calc(100% - var(--coach-outer-top) - var(--coach-outer-bottom))");
    }

    [Fact]
    public void Fullscreen_sam_keeps_its_independent_fixed_surface_insets()
    {
        var block = CssBlock(".sam-panel--fullscreen");

        block.Should().Contain("padding-top: env(safe-area-inset-top, 0px)");
        block.Should().Contain("padding-right: env(safe-area-inset-right, 0px)");
        block.Should().Contain("padding-bottom: env(safe-area-inset-bottom, 0px)");
        block.Should().Contain("padding-left: env(safe-area-inset-left, 0px)");
    }

    [Fact]
    public void Compact_and_expanded_sam_keep_independent_inline_and_bottom_insets()
    {
        var block = CssBlock(".sam-panel");

        block.Should().Contain("padding-left: env(safe-area-inset-left, 0px)");
        block.Should().Contain("padding-right: env(safe-area-inset-right, 0px)");
        block.Should().Contain("padding-bottom: env(safe-area-inset-bottom, 0px)");
    }

    [Fact]
    public void Compact_and_expanded_sam_cap_height_below_the_dynamic_top_inset()
    {
        var compact = CssBlock(".sam-panel--compact");
        compact.Should().Contain(
            "max-height: calc(100dvh - 1rem - var(--safe-area-top))");

        var expanded = CssBlock(".sam-panel--expanded");
        expanded.Should().Contain(
            "max-height: calc(100dvh - max(60px, calc(1rem + var(--safe-area-top))))");
    }

    [Fact]
    public void Non_fullscreen_sam_does_not_duplicate_top_or_bottom_inset_padding()
    {
        var compact = CssBlock(".sam-panel--compact");
        var expanded = CssBlock(".sam-panel--expanded");

        compact.Should().NotContain("padding-top");
        compact.Should().NotContain("padding-bottom");
        expanded.Should().NotContain("padding-top");
        expanded.Should().NotContain("padding-bottom");
    }

    [Fact]
    public void Hybrid_host_opts_into_css_owned_edge_to_edge_layout()
    {
        var hostPage = File.ReadAllText(Path.Combine(
            Root, "src", "SentenceStudio.AppLib", "wwwroot", "index.html"));
        var nativePage = File.ReadAllText(Path.Combine(
            Root, "src", "SentenceStudio.iOS", "BlazorHostPage.cs"));

        hostPage.Should().Contain("viewport-fit=cover");
        nativePage.Should().Contain("SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None");
    }

    private static string CssBlock(string selector)
        => CssBlock(Css, selector);

    private static string CssBlock(
        string selector,
        Func<string, bool> predicate)
        => CssBlock(Css, selector, predicate);

    private static string CssBlock(
        string source,
        string selector,
        Func<string, bool>? predicate = null)
    {
        var pattern = Regex.Escape(selector) + @"\s*\{([^}]*)\}";
        var blocks = Regex.Matches(source, pattern)
            .Select(match => match.Groups[1].Value)
            .Where(block => predicate?.Invoke(block) ?? true)
            .ToArray();
        blocks.Should().NotBeEmpty($"the stylesheet must define {selector}");
        return blocks[0];
    }

    private static IReadOnlyList<string> CssBlocks(string selector)
    {
        var pattern = @"(?m)^[ \t]*" + Regex.Escape(selector) + @"\s*\{([^}]*)\}";
        return Regex.Matches(Css, pattern)
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static string CssMediaBlock(string mediaQuery)
    {
        var start = Css.IndexOf(mediaQuery, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the stylesheet must define {mediaQuery}");

        var openingBrace = Css.IndexOf('{', start + mediaQuery.Length);
        openingBrace.Should().BeGreaterThan(start);
        return BalancedBlockContents(Css, openingBrace);
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
