using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace SentenceStudio.UI.Tests.Layout;

/// <summary>
/// Sam's FAB is fixed to the viewport's bottom-right corner, which is where every activity puts its
/// primary action. It covered Conversation's Send button, and Grade / Next / the correct-count
/// badge on the activity shell's bars. These tests hold the reservation that keeps bottom bars
/// clear of it, and the input-row rule that stops squeezed buttons losing their right padding.
/// </summary>
/// <remarks>
/// Asserted against the stylesheet and Razor text: there is no layout engine in this suite. The
/// geometry tests do the same arithmetic the browser does from the parsed values, so a change to
/// the FAB's size or inset that the bars do not absorb fails here. On-device confirmation lives in
/// the e2e-testing skill (other-activities.md, Conversation).
/// </remarks>
public class SamFabClearanceContractTests
{
    private const double RemPx = 16;

    private static readonly string Root = RepoRoot();
    private static readonly string UiRoot = Path.Combine(Root, "src", "SentenceStudio.UI");
    private static readonly string Css = StripComments(File.ReadAllText(
        Path.Combine(UiRoot, "wwwroot", "css", "app.css")));
    private static readonly string Conversation = File.ReadAllText(
        Path.Combine(UiRoot, "Pages", "Conversation.razor"));
    private static readonly string Shadowing = File.ReadAllText(
        Path.Combine(UiRoot, "Pages", "Shadowing.razor"));

    // ─── The FAB's footprint is declared once ───

    [Fact]
    public void Fab_size_and_inset_are_root_variables()
    {
        var root = CssBlock(":root", block => block.Contains("--sam-fab-size"));
        root.Should().Contain("--sam-fab-size: 48px");
        root.Should().Contain("--sam-fab-inset: 1.5rem");
    }

    [Fact]
    public void Fab_reads_its_footprint_from_the_variables()
    {
        var fab = CssBlock(".sam-fab");
        fab.Should().Contain("width: var(--sam-fab-size)");
        fab.Should().Contain("height: var(--sam-fab-size)");
        fab.Should().Contain("bottom: calc(var(--sam-fab-inset) + env(safe-area-inset-bottom, 0px))");
        fab.Should().Contain("right: calc(var(--sam-fab-inset) + env(safe-area-inset-right, 0px))");
        fab.Should().NotMatchRegex(@"(?m)^\s*(bottom|right):\s*[\d.]+rem\s*;",
            "a literal inset beside the variable would let the FAB move without the bars following");
    }

    [Fact]
    public void Phones_shrink_the_inset_through_the_variable_not_a_second_fab_rule()
    {
        var phone = MediaBlock("(max-width: 575.98px)", body => body.Contains("--sam-fab-inset"));
        phone.Should().Contain("--sam-fab-inset: 1rem");
        Regex.IsMatch(phone, @"(?m)^\s*\.sam-fab\s*\{").Should().BeFalse(
            "the phone inset must reach the bars too, which only the variable does");
    }

    // ─── Bottom bars reserve the FAB's corner while it is on screen ───

    [Fact]
    public void Clearance_is_derived_from_the_fab_footprint_and_only_exists_with_a_fab()
    {
        var clearance = CssBlock("body:has(.sam-fab)");
        clearance.Should().Contain(
            "--sam-fab-clearance: calc(var(--sam-fab-inset) + var(--sam-fab-size) + 0.75rem)");
    }

    [Theory]
    [InlineData(".activity-input-bar")]
    [InlineData(".activity-footer")]
    public void Activity_shell_bars_give_up_their_right_edge_to_the_fab(string bar)
    {
        var block = CssBlock($"body:has(.sam-fab) {bar}", selectorList: true);
        block.Should().Contain("padding-right: var(--sam-fab-clearance)");
    }

    [Fact]
    public void Conversation_composer_gives_up_its_right_edge_to_the_fab()
    {
        var block = CssBlock("body:has(.sam-fab) .conversation-composer");
        block.Should().Contain("padding-right: calc(var(--sam-fab-clearance) - 1rem)");
    }

    [Fact]
    public void Conversation_composer_uses_the_clearing_class_and_no_important_padding_utility()
    {
        var composer = Regex.Match(Conversation, @"<div class=""([^""]*conversation-composer[^""]*)""");
        composer.Success.Should().BeTrue("the composer card must carry .conversation-composer");

        var classes = composer.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        classes.Should().NotContain(c => Regex.IsMatch(c, @"^p[xe]?-\d$"),
            "Bootstrap padding utilities are !important and would cancel the FAB clearance");
        Conversation.Should().NotContain("padding-bottom: calc(0.75rem + env(safe-area-inset-bottom, 0px)) !important",
            "the composer's padding lives in .conversation-composer, not an inline !important style");

        var block = CssBlock(".conversation-composer");
        block.Should().Contain("padding-bottom: calc(0.75rem + env(safe-area-inset-bottom, 0px))");
    }

    /// <summary>
    /// Desktop and phone, with the numbers the browser uses. Bars bleed to main's edge (the FAB's
    /// reference line); the composer card sits inside main's inline padding.
    /// </summary>
    [Theory]
    [InlineData("desktop", 1.0, 0.0)]
    [InlineData("phone", 0.75, -0.25)]
    public void Every_bar_keeps_at_least_8px_between_its_controls_and_the_fab(
        string viewport, double mainPaddingRem, double barEdgeRem)
    {
        var size = Px(RootValue("--sam-fab-size"));
        var inset = viewport == "phone"
            ? Px(Regex.Match(MediaBlock("(max-width: 575.98px)", b => b.Contains("--sam-fab-inset")),
                @"--sam-fab-inset:\s*([^;]+);").Groups[1].Value)
            : Px(RootValue("--sam-fab-inset"));
        var gap = Px(Regex.Match(CssBlock("body:has(.sam-fab)"),
            @"var\(--sam-fab-size\)\s*\+\s*([\d.]+rem)\)").Groups[1].Value);
        var clearance = inset + size + gap;
        var fabNearEdge = inset + size; // distance of the FAB's left side from main's right edge

        // Shell bars: their right edge sits barEdgeRem from main's edge (negative = bleeds past it).
        var barContentEdge = barEdgeRem * RemPx + clearance;
        (barContentEdge - fabNearEdge).Should().BeGreaterThanOrEqualTo(8,
            $"on {viewport}, bar controls must end at least 8px before the FAB");

        // Conversation composer: card edge is main's inline padding in from main's edge.
        var composerContentEdge = mainPaddingRem * RemPx + (clearance - 1 * RemPx);
        (composerContentEdge - fabNearEdge).Should().BeGreaterThanOrEqualTo(8,
            $"on {viewport}, composer controls must end at least 8px before the FAB");
    }

    // ─── Squeezed buttons keep their labels inside their padding ───

    [Fact]
    public void Input_rows_let_the_text_field_give_way_instead_of_the_buttons()
    {
        var shrink = CssBlock(".activity-input-bar form > .btn", selectorList: true);
        shrink.Should().Contain("flex-shrink: 0");
        Selectors(".activity-input-bar form > .btn").Should().Contain(new[]
        {
            ".conversation-composer .btn-ss-primary",
            ".conversation-composer .btn-ss-secondary",
        });

        var give = CssBlock(".activity-input-bar form > .form-control", selectorList: true);
        give.Should().Contain("min-width: 0");
        Selectors(".activity-input-bar form > .form-control").Should().Contain(
            ".conversation-composer .form-control");
    }

    [Fact]
    public void Ss_buttons_still_set_the_min_width_that_makes_the_flex_shrink_rule_necessary()
    {
        // If this ever stops being true the squeeze rule above is redundant, not wrong — but the
        // reason it exists should be re-read before anyone removes it.
        CssBlock(".btn-ss-primary").Should().Contain("min-width: 44px");
        CssBlock(".btn-ss-secondary").Should().Contain("min-width: 44px");
    }

    [Fact]
    public void Conversation_send_and_voice_drop_to_icons_below_md_with_an_accessible_name()
    {
        Conversation.Should().Contain("aria-label='@Localize[\"Conversation_Send\"]'");
        Conversation.Should().Contain("<i class=\"bi bi-send d-md-none\" aria-hidden=\"true\"></i>");
        Conversation.Should().Contain("<span class=\"d-none d-md-inline\">@Localize[\"Conversation_Send\"]</span>");
        Conversation.Should().Contain("aria-label='@Localize[\"Conversation_VoiceStart\"]'");
    }

    [Fact]
    public void Shadowing_footer_fits_beside_the_fab_on_a_phone()
    {
        Shadowing.Should().Contain("shadowing-controls");
        Shadowing.Should().Contain("aria-label='@Localize[\"Shadowing_Prev\"]'");
        Shadowing.Should().Contain("aria-label='@Localize[\"Shadowing_Next\"]'");
        Shadowing.Should().Contain("<span class=\"d-none d-sm-inline ms-1\">@Localize[\"Shadowing_Prev\"]</span>");
        Shadowing.Should().Contain("<span class=\"d-none d-sm-inline me-1\">@Localize[\"Shadowing_Next\"]</span>");
        Shadowing.Should().Contain("btn btn-ss-primary px-3 px-sm-4");

        var phone = MediaBlock("(max-width: 575.98px)", b => b.Contains(".shadowing-controls"));
        phone.Should().MatchRegex(@"\.shadowing-controls \.btn\s*\{[^}]*padding-left: 0\.5rem;[^}]*padding-right: 0\.5rem;");
    }

    // ─── Helpers ───

    private static string CssBlock(string selector, bool selectorList = false) =>
        CssBlock(selector, _ => true, selectorList);

    private static string CssBlock(string selector, Func<string, bool> where, bool selectorList = false)
    {
        // Top-level rules only: the selector starts a line, optionally as one entry of a list.
        var escaped = Regex.Escape(selector);
        var pattern = selectorList
            ? @"(?m)^[ \t]*(?:[^{}\n]+,\s*\n[ \t]*)*" + escaped + @"\s*(?:,[^{]*)?\{([^}]*)\}"
            : @"(?m)^[ \t]*" + escaped + @"\s*\{([^}]*)\}";
        var match = Regex.Matches(Css, pattern)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(where);

        match.Should().NotBeNull($"the stylesheet must define {selector}");
        return match!;
    }

    private static IReadOnlyList<string> Selectors(string member)
    {
        var escaped = Regex.Escape(member);
        var match = Regex.Match(Css, @"(?m)((?:^[^{}\n]+,\s*\n)*^[^{}\n]*" + escaped + @"[^{]*)\{");
        match.Success.Should().BeTrue($"the stylesheet must have a rule listing {member}");
        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string MediaBlock(string query, Func<string, bool> where)
    {
        var bodies = new List<string>();
        var start = 0;
        var head = "@media " + query;
        while ((start = Css.IndexOf(head, start, StringComparison.Ordinal)) >= 0)
        {
            var open = Css.IndexOf('{', start);
            var depth = 0;
            var i = open;
            for (; i < Css.Length; i++)
            {
                if (Css[i] == '{') depth++;
                else if (Css[i] == '}' && --depth == 0) break;
            }
            bodies.Add(Css.Substring(open + 1, i - open - 1));
            start = i;
        }

        var body = bodies.FirstOrDefault(where);
        body.Should().NotBeNull($"the stylesheet must have an @media {query} block for this rule");
        return body!;
    }

    private static string RootValue(string variable)
    {
        var root = CssBlock(":root", block => block.Contains(variable));
        return Regex.Match(root, Regex.Escape(variable) + @":\s*([^;]+);").Groups[1].Value;
    }

    private static double Px(string value)
    {
        value = value.Trim();
        if (value.EndsWith("rem", StringComparison.Ordinal))
            return double.Parse(value[..^3], CultureInfo.InvariantCulture) * RemPx;
        if (value.EndsWith("px", StringComparison.Ordinal))
            return double.Parse(value[..^2], CultureInfo.InvariantCulture);
        throw new FormatException($"Unexpected CSS length '{value}'");
    }

    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
