using System.Text.RegularExpressions;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Contract tests for the overlay's stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// Three of these come from design review on 2026-08-20 and are all cases where the rendered
/// markup is correct and the styling is not, which no render test can see:
/// </para>
/// <list type="bullet">
/// <item>the compact panel's fixed 420px height puts its header off the top of a phone in
/// landscape, where the viewport is about 390px tall and too wide to match the &lt;576px rules;</item>
/// <item>the entry control painted a literal white glyph on <c>--bs-primary</c>, which in the
/// brite theme is <c>#a2e436</c> — a light green that white does not read on;</item>
/// <item>the header's 28px controls are below the 44px floor this repo applies to every coach
/// control, on a surface reached primarily by thumb.</item>
/// </list>
/// <para>
/// Asserted against the stylesheet text rather than a rendered box because there is no layout
/// engine in this suite. That is a real limit: these pin the declarations, and the visual result
/// is confirmed in the browser (`SAM-VIS-*`).
/// </para>
/// </remarks>
public class SamOverlayStyleContractTests
{
    private static readonly string Css = LoadCss();

    private static string LoadCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("repository root not found"),
            "src", "SentenceStudio.UI", "wwwroot", "css", "app.css");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// The declarations of the first rule whose selector list contains <paramref name="selector"/>.
    /// </summary>
    /// <param name="within">
    /// Optional at-rule prelude (for example <c>pointer: coarse</c>) to search inside, so a rule
    /// that only applies on touch is not confused with the base rule of the same name.
    /// </param>
    private static string Block(string selector, string? within = null)
    {
        var source = within is null ? Css : AtRuleBody(within);

        // Selector lists are comma separated and the selector may be one of several, so the match
        // is anchored on a boundary rather than on the whole prelude.
        var pattern = Regex.Escape(selector) + @"(?:\s*,\s*[^{]+)?\s*\{([^}]*)\}";
        var match = Regex.Matches(source, pattern)
            .Cast<Match>()
            .FirstOrDefault(m => SelectorListOf(source, m).Contains(selector, StringComparison.Ordinal));

        match.Should().NotBeNull($"the stylesheet must define {selector}"
            + (within is null ? string.Empty : $" inside @media ({within})"));

        return match!.Groups[1].Value;
    }

    private static string SelectorListOf(string source, Match match)
    {
        var start = source.LastIndexOf('}', match.Index);
        var open = source.IndexOf('{', match.Index);
        return source[(start + 1)..open];
    }

    /// <summary>The body of the first at-rule whose prelude contains <paramref name="prelude"/>.</summary>
    private static string AtRuleBody(string prelude)
    {
        var bodies = AtRuleBodies(prelude);
        bodies.Should().NotBeEmpty($"the stylesheet must have an @media ({prelude}) block");
        return bodies[0];
    }

    /// <summary>
    /// Every at-rule body whose prelude contains <paramref name="prelude"/>.
    /// </summary>
    /// <remarks>
    /// The stylesheet has several reduced-motion blocks, each next to the animation it turns off,
    /// which is the right place for them and means a question about one of them cannot be answered
    /// by reading only the first.
    /// </remarks>
    private static IReadOnlyList<string> AtRuleBodies(string prelude)
    {
        var bodies = new List<string>();
        var marker = $"@media ({prelude})";
        var from = 0;

        while (true)
        {
            var start = Css.IndexOf(marker, from, StringComparison.Ordinal);
            if (start < 0)
            {
                return bodies;
            }

            var open = Css.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < Css.Length; i++)
            {
                if (Css[i] == '{')
                {
                    depth++;
                }
                else if (Css[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                    {
                        bodies.Add(Css[(open + 1)..i]);
                        from = i;
                        break;
                    }
                }
            }

            if (from <= start)
            {
                throw new InvalidOperationException($"unterminated {marker} block");
            }
        }
    }

    private static IReadOnlyList<string> Declarations(string block, string property) =>
        Regex.Matches(block, $@"(?m)^\s*{Regex.Escape(property)}\s*:\s*([^;]+);")
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

    // ================================================================ phone landscape

    [Fact]
    public void TheCompactPanelIsCappedToTheViewportSoItFitsPhoneLandscape()
    {
        var block = Block(".sam-panel--compact");

        block.Should().Contain("height: 420px",
            "the compact size is still a fixed height; the cap is what keeps it on screen");

        var caps = Declarations(block, "max-height");

        caps.Should().HaveCount(2,
            "a vh line for engines without dynamic viewport units, then the dvh line that wins "
            + "where it is supported");
        caps[0].Should().Contain("100vh");
        caps[1].Should().Contain("100dvh");
    }

    [Fact]
    public void TheExpandedPanelCapIsAlsoExpressedInDynamicViewportUnits()
    {
        var block = Block(".sam-panel--expanded");

        var heights = Declarations(block, "height");
        heights.Should().HaveCount(2);
        heights[1].Should().Contain("dvh");

        var caps = Declarations(block, "max-height");
        caps.Should().HaveCount(2);
        caps[0].Should().Contain("100vh", "the fallback keeps the previous behaviour, not none");
        caps[1].Should().Contain("100dvh");
    }

    [Fact]
    public void TheFullScreenPanelUsesDynamicViewportUnitsWithAFallback()
    {
        var block = Block(".sam-panel--fullscreen");

        var heights = Declarations(block, "height");
        heights.Should().HaveCount(2);
        heights[0].Should().Be("100vh");
        heights[1].Should().Be("100dvh");
    }

    [Fact]
    public void TheSmallViewportPanelIsCappedToo()
    {
        var block = Block(".sam-panel--compact", within: "max-width: 575.98px");

        Declarations(block, "height").Last().Should().Contain("dvh");
        Declarations(block, "max-height").Should().ContainSingle()
            .Which.Should().Contain("100dvh");
    }

    // ================================================================ contrast

    [Fact]
    public void TheEntryControlTakesItsForegroundFromTheThemeNotFromWhite()
    {
        var block = Block(".sam-fab");

        var colors = Declarations(block, "color");

        colors.Should().ContainSingle();
        colors[0].Should().Be("var(--ss-on-primary, #fff)",
            "brite's primary is a light green that white does not read on; --ss-on-primary is the "
            + "token every other primary surface in this stylesheet already pairs with it");
    }

    [Fact]
    public void TheUnreadBadgeRingAlsoFollowsTheThemeForeground()
    {
        var block = Block(".sam-fab__badge");

        Declarations(block, "border").Should().ContainSingle()
            .Which.Should().Contain("var(--ss-on-primary",
                "the ring separates the badge from the button beneath it, so it is the button's "
                + "colour rather than a literal white");
    }

    [Fact]
    public void BriteOverridesTheOnPrimaryTokenToADarkForeground()
    {
        // The whole point of reading the token: brite is the theme where white fails.
        var block = Block("[data-ss-theme=\"brite\"]");

        Declarations(block, "--ss-on-primary").Should().ContainSingle()
            .Which.Should().Be("#000");
    }

    [Fact]
    public void NoSamSurfaceHardcodesAWhiteForeground()
    {
        var samSection = Css[Css.IndexOf("Sam Overlay — persistent FAB", StringComparison.Ordinal)..];
        var end = samSection.IndexOf("Sam: proposed changes", StringComparison.Ordinal);
        samSection = end > 0 ? samSection[..end] : samSection;

        Regex.Matches(samSection, @"(?m)^\s*color\s*:\s*#fff\b")
            .Should().BeEmpty("a literal white foreground cannot follow the theme");
    }

    // ================================================================ touch targets

    [Fact]
    public void TheHeaderControlsReachTheRepositoryTouchFloorOnATouchPointer()
    {
        var block = Block(".sam-panel__btn", within: "pointer: coarse");

        Declarations(block, "min-width").Should().ContainSingle().Which.Should().Be("44px");
        Declarations(block, "min-height").Should().ContainSingle().Which.Should().Be("44px");
        Declarations(block, "width").Should().ContainSingle().Which.Should().Be("44px");
        Declarations(block, "height").Should().ContainSingle().Which.Should().Be("44px");
    }

    [Fact]
    public void DesktopKeepsTheDenseHeader()
    {
        var block = Block(".sam-panel__btn");

        Declarations(block, "width").Should().ContainSingle().Which.Should().Be("28px");
        Declarations(block, "height").Should().ContainSingle().Which.Should().Be("28px");
    }

    /// <summary>
    /// Keyed on the pointer rather than on a width, for the reason the repo already gives for
    /// <c>.coach-action</c>: an iPad at 768-991px is a touch device that a width rule misses.
    /// </summary>
    [Fact]
    public void TheTouchFloorIsKeyedOnThePointerNotOnABreakpoint()
    {
        Css.Should().Contain("@media (pointer: coarse)");
    }

    [Fact]
    public void TheHeaderStillFitsItsControlsAtTheCompactWidth()
    {
        // 360px compact width, three 44px controls, two 0.25rem gaps and 2 x 0.5rem padding.
        const int compactWidth = 360;
        const int controls = 3 * 44;
        const int gaps = 2 * 4;
        const int padding = 2 * 8;

        (compactWidth - controls - gaps - padding).Should().BeGreaterThan(120,
            "the title needs room to be a title, not an ellipsis");

        // And if a future persona name is long, the title yields rather than the controls.
        var title = Block(".sam-panel__title");
        Declarations(title, "min-width").Should().ContainSingle().Which.Should().Be("0");
        Declarations(title, "text-overflow").Should().ContainSingle().Which.Should().Be("ellipsis");

        Declarations(Block(".sam-panel__controls"), "flex").Should().ContainSingle()
            .Which.Should().Be("0 0 auto", "the controls are the fixed part of the header");
    }

    [Fact]
    public void TheHeaderPaddingComesDownWhenTheControlsGrow()
    {
        // The controls set the header height on touch, and a phone in landscape cannot spare
        // 44px of control plus 16px of padding above the conversation.
        var block = Block(".sam-panel__header", within: "pointer: coarse");

        Declarations(block, "padding").Should().ContainSingle()
            .Which.Should().Be("0.25rem 0.5rem");
    }

    // ================================================================ the jump control

    [Fact]
    public void TheJumpControlMeetsTheTouchFloorAtEveryWidth()
    {
        var block = Block(".coach-jump-latest");

        Declarations(block, "min-width").Should().ContainSingle().Which.Should().Be("44px");
        Declarations(block, "min-height").Should().ContainSingle().Which.Should().Be("44px");
    }

    [Fact]
    public void MotionIsRemovedForAReaderWhoAskedForLess()
    {
        var reduced = string.Join("\n", AtRuleBodies("prefers-reduced-motion: reduce"));

        reduced.Should().Contain(".coach-jump-latest");
        reduced.Should().Contain(".sam-fab");
    }
}
