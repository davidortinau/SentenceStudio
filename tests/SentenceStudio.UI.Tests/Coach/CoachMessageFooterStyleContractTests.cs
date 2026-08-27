using System.Text.RegularExpressions;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Contract tests for the message footer's stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// The rejected revision rendered the report panel as a third item in the action row's flex line,
/// squeezed beside Copy and the flag. <see cref="CoachReportEscapeAndLayoutTests"/> pins the tree
/// shape that fixes it; this pins the styling that shape depends on. Both are needed: a correct
/// tree inside a flex container is still a squeezed panel, and the render tests cannot see a
/// stylesheet.
/// </para>
/// <para>
/// Asserted against the stylesheet text rather than a rendered box, for the same reason
/// <see cref="SamOverlayStyleContractTests"/> gives: there is no layout engine in this suite. These
/// pin the declarations; the visual result is confirmed in the browser.
/// </para>
/// </remarks>
public class CoachMessageFooterStyleContractTests
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

    private static string Block(string selector, string? within = null)
    {
        var source = within is null ? Css : AtRuleBody(within);
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

    private static string AtRuleBody(string prelude)
    {
        var marker = $"@media ({prelude})";
        var start = Css.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the stylesheet must have an @media ({prelude}) block");

        var open = Css.IndexOf('{', start);
        var depth = 0;

        for (var i = open; i < Css.Length; i++)
        {
            if (Css[i] == '{')
            {
                depth++;
            }
            else if (Css[i] == '}' && --depth == 0)
            {
                return Css[(open + 1)..i];
            }
        }

        throw new InvalidOperationException($"unterminated {marker} block");
    }

    private static IReadOnlyList<string> Declarations(string block, string property) =>
        Regex.Matches(block, $@"(?m)^\s*{Regex.Escape(property)}\s*:\s*([^;]+);")
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

    // ================================================================ the footer is not a row

    /// <summary>
    /// The footer wraps the action row and the panel. If it were a flex row itself, moving the panel
    /// out of <c>.coach-message-actions</c> would only have moved the problem up one level.
    /// </summary>
    [Fact]
    public void TheFooterStacksItsChildrenRatherThanLiningThemUp()
    {
        var footer = Block(".coach-message-footer");

        Declarations(footer, "display").Should().ContainSingle().Which
            .Should().Be("block",
                "the row and the panel are stacked; a flex footer would put the form beside the buttons again");
    }

    /// <summary>
    /// The action row is still a row. Only the panel left it.
    /// </summary>
    [Fact]
    public void TheActionRowIsStillAFlexLine()
    {
        Declarations(Block(".coach-message-actions"), "display").Should().Contain("flex");
    }

    /// <summary>
    /// A block-level panel takes the message column's width instead of whatever a flex line leaves
    /// over, and the reading-width cap keeps it from running to the full width of a desktop
    /// workspace.
    /// </summary>
    [Fact]
    public void ThePanelTakesTheMessageColumnUpToAReadingWidth()
    {
        var panel = Block(".coach-report-panel");

        Declarations(panel, "display").Should().Contain("block");
        Declarations(panel, "max-width").Should().ContainSingle().Which
            .Should().Be("68ch", "a form line that runs the width of a desktop workspace is hard to read");
        Declarations(panel, "width").Should().BeEmpty(
            "a block element already fills its column; a width here would fight the cap");
    }

    // ================================================================ focus is visible

    /// <summary>
    /// The settled state is the focus target after a successful report, and it is a span rather than
    /// a button — so nothing gives it a focus ring unless the stylesheet does.
    /// </summary>
    [Fact]
    public void TheSettledStateShowsAFocusRingWhenScriptFocusesIt()
    {
        var ring = Block(".coach-report-done:focus-visible");

        Declarations(ring, "outline").Should().ContainSingle().Which
            .Should().Contain("2px", "a focus target with no visible ring moves focus somewhere invisible");
    }

    // ================================================================ touch targets

    /// <summary>
    /// Every control on the row is reached by thumb on a phone.
    /// </summary>
    [Theory]
    [InlineData(".coach-report-flag")]
    [InlineData(".coach-report-done")]
    [InlineData(".coach-evidence-toggle")]
    public void TheRowsControlsMeetTheTouchFloor(string selector)
    {
        Declarations(Block(selector), "min-height").Should().Contain("44px");
    }

    /// <summary>
    /// The reason rows are denser on a mouse and comfortable on a finger, which is what the
    /// pointer-keyed block is for.
    /// </summary>
    [Fact]
    public void TheReasonRowsReachTheTouchFloorOnACoarsePointer()
    {
        Declarations(Block(".coach-report-reason", within: "pointer: coarse"), "min-height")
            .Should().Contain("44px");

        Declarations(Block(".coach-report-reason"), "min-height")
            .Should().NotContain("44px",
                "five 44px rows on a desktop would fill the message column on their own");
    }

    // ================================================================ themes

    /// <summary>
    /// The defect this guards against is already in the repo's history: a control painted a literal
    /// white glyph, which vanished on the brite theme's light green primary.
    /// </summary>
    /// <remarks>
    /// Contrast itself cannot be computed here — the tokens resolve per theme at runtime and there
    /// is no renderer. What can be checked is that these controls never opt out of the theme in the
    /// first place, which is the only way the earlier defect happened.
    /// </remarks>
    [Theory]
    [InlineData(".coach-report-flag")]
    [InlineData(".coach-report-done")]
    [InlineData(".coach-report-panel")]
    [InlineData(".coach-evidence-toggle")]
    [InlineData(".coach-evidence-inline")]
    public void TheseControlsTakeTheirColoursFromTheTheme(string selector)
    {
        var block = Block(selector);

        foreach (var property in new[] { "color", "background-color", "border", "border-inline-start" })
        {
            foreach (var value in Declarations(block, property))
            {
                value.Should().NotMatchRegex(@"#[0-9a-fA-F]{3,8}\b",
                    $"{selector}'s {property} must follow the theme, not name a colour");
                value.Should().NotMatchRegex(@"\b(?:white|black)\b",
                    $"{selector}'s {property} must follow the theme, not name a colour");
            }
        }
    }
}
